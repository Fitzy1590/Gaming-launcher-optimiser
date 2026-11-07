using System.Diagnostics;
using FitzLauncher.Core.Fps;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

public readonly record struct LiveStats(double Fps, double OnePercentLow, double? CpuTempC, double? GpuTempC, double? GpuLoad, TimeSpan Elapsed, int? Cap, bool ThermalThrottling, int? BenchmarkSecondsLeft, string? Note = null);
public readonly record struct TempAlert(string Component, double TempC, double ThresholdC, string Explanation = "");

/// <summary>
/// One play session from launch to exit: Game Mode on → start game → pin cores → track processes,
/// FPS and temperatures (thermal guard, RTSS overlay line, hotkey benchmarks) → Game Mode off →
/// write a compact session summary.
/// </summary>
public sealed class GameSession(
    Game game,
    LauncherSettings settings,
    HardwareProfile? hardware,
    GameModeController gameMode,
    TemperatureMonitor temps,
    LibraryStore store,
    IReadOnlyList<LogicalProcessor> cpuTopology)
{
    public event Action<string>? Status;
    public event Action<LiveStats>? Live;
    public event Action<TempAlert>? Alert;
    public event Action<BenchmarkResult>? BenchmarkCompleted;

    /// <summary>One-launch options from the display plan (e.g. Unity's -screen-width).</summary>
    public string ExtraArguments { get; init; } = "";
    /// <summary>Cap for the active screen; null = the game's own target.</summary>
    public int? EffectiveCap { get; init; }
    public DisplayInfo? Display { get; init; }
    public int RenderScale { get; init; } = 100;

    /// <summary>APU power limit for this session (watts), applied through RyzenAdj.</summary>
    public int? PowerWatts { get; init; }
    public int PowerMaxWatts { get; init; } = 120;
    public RyzenAdjClient? RyzenAdj { get; init; }
    /// <summary>RTSS limiter mode written for this session (0 async, 1 front-edge sync), for learning.</summary>
    public int? LimiterMode { get; init; }

    /// <summary>Curve Optimizer all-core offset for this session (negative = undervolt), via RyzenAdj.</summary>
    public int? UndervoltOffset { get; init; }
    /// <summary>When the controller was last touched (for the adaptive idle cap).</summary>
    public Func<DateTime>? LastControllerInput { get; private set; }

    public GameSession WithControllerInput(Func<DateTime>? lastInput)
    {
        LastControllerInput = lastInput;
        return this;
    }
    /// <summary>True if the undervolt was active this session (for crash handling).</summary>
    public bool UndervoltApplied { get; private set; }

    /// <summary>Target chosen in-game with the quick switch, if any (kept as the game's new target).</summary>
    public int? FinalTarget => _quickCap;

    private int? Cap => _quickCap ?? EffectiveCap ?? game.FpsTarget;

    // Live state shared with the quick switch (called from the controller/hotkey threads).
    private readonly Lock _capLock = new();
    private RtssClient? _rtss;
    private string _renderExe = "";
    private ThermalGovernor? _guard;
    private int? _quickCap;
    private string? _note;
    private DateTime _noteUntil;

    /// <summary>In-game target switch: +1 = next target up, -1 = down. Applied instantly through RTSS.</summary>
    public void ChangeTarget(int direction)
    {
        lock (_capLock)
        {
            if (_rtss is null) { Note("FPS target switch needs RTSS"); return; }
            var maxHz = Display?.MaxRefreshHz ?? 0;
            int[] ladder = [.. new[] { 60 }.Concat(LauncherSettings.FpsTargets).Where(t => maxHz <= 0 || t <= maxHz - 3)];
            var current = Cap ?? ladder[^1];
            var idx = Array.FindIndex(ladder, t => t >= current);
            if (idx < 0) idx = ladder.Length - 1;
            var next = ladder[Math.Clamp(idx + direction, 0, ladder.Length - 1)];
            if (next == current) { Note($"Target {current}"); return; }
            if (!_rtss.SetFramerateLimit(_renderExe, next)) { Note("Couldn't change the cap"); return; }
            _quickCap = next;
            if (_guard is not null) _guard = new ThermalGovernor(next, settings.ThermalGuard, settings.CpuTempAlertC, settings.GpuTempAlertC);
            Note($"Target {next}");
            Status?.Invoke($"FPS target changed to {next} in-game");
        }
    }

    private int? _livePower;
    private bool _hud;

    /// <summary>Quick HUD (View + LB held): shows the target and power line until released.</summary>
    public void SetHud(bool open) => _hud = open;

    /// <summary>In-game power change, ±5 W per press (needs RyzenAdj).</summary>
    public void ChangePower(int direction)
    {
        lock (_capLock)
        {
            if (RyzenAdj is null || _livePower is not { } now) { Note("Power control needs RyzenAdj"); return; }
            var next = Math.Clamp(now + direction * 5, 10, PowerMaxWatts);
            if (next == now) { Note($"Power {now} W"); return; }
            if (!RyzenAdj.Apply(next, PowerMaxWatts)) { Note("Couldn't change power"); return; }
            _livePower = next;
            Note($"Power {next} W");
            Status?.Invoke($"Power limit changed to {next} W in-game");
        }
    }

    /// <summary>
    /// After sleep/resume the firmware resets power limits and the Curve Optimizer, and RTSS can drop
    /// its cap; put ours back.
    /// </summary>
    public void OnResume()
    {
        lock (_capLock)
        {
            if (RyzenAdj is not null && _livePower is { } w) RyzenAdj.Apply(w, PowerMaxWatts);
            if (RyzenAdj is not null && UndervoltApplied && UndervoltOffset is { } uv) RyzenAdj.ApplyUndervolt(uv);
            if (_rtss is not null && (_guard?.CurrentCap ?? Cap) is { } cap) { _rtss.ReloadProfile(_renderExe); _rtss.SetFramerateLimit(_renderExe, cap); }
            Note("Settings restored after sleep");
        }
        Status?.Invoke("Resumed from sleep: power, undervolt and FPS cap re-applied");
    }

    private void Note(string text)
    {
        _note = text;
        _noteUntil = DateTime.Now.AddSeconds(4);
    }

    private EtwFpsTracker? _fps;
    private FrameTimeRecorder? _bench;
    private DateTimeOffset _benchStart, _benchEnd;
    private double? _benchMaxGpu;
    private readonly Lock _benchLock = new();

    /// <summary>Starts a fixed-length capture alongside the session (benchmark hotkey).</summary>
    public bool StartBenchmark(int seconds)
    {
        lock (_benchLock)
        {
            if (_fps is null || _bench is not null) return false;
            _bench = new FrameTimeRecorder();
            _benchStart = DateTimeOffset.Now;
            _benchEnd = _benchStart.AddSeconds(Math.Clamp(seconds, 10, 600));
            _benchMaxGpu = null;
            _fps.SetSink(_bench);
        }
        Status?.Invoke($"Benchmark started ({seconds}s)");
        Native.MessageBeep(0x40);
        return true;
    }

    public async Task<SessionSummary> RunAsync(bool launchAsAdmin, CancellationToken ct)
    {
        var useGameMode = settings.GameMode.Enabled && game.UseGameMode;
        var tuning = SystemAdvisor.Tune(hardware);
        var installDir = GameScanner.ResolveInstallRoot(game);
        var renderExe = Path.GetFileName(game.LastScan?.RenderExePath ?? game.ExePath);

        if (useGameMode)
        {
            Status?.Invoke("Game Mode: preparing…");
            foreach (var line in await Task.Run(() => gameMode.ActivatePreLaunch(settings.GameMode, tuning, settings.CoreParkingOff), ct)) Status?.Invoke(line);
        }

        var start = DateTimeOffset.Now;
        int pid;
        try
        {
            var args = $"{game.Arguments} {game.OptimizerArguments} {ExtraArguments}".Trim();
            pid = GameLauncher.Start(game.ExePath, args, launchAsAdmin);
        }
        catch
        {
            if (useGameMode) gameMode.Restore();
            throw;
        }
        Status?.Invoke($"Started {game.Name}");

        var tracker = new GameProcessTracker(pid, installDir);
        tracker.Refresh();
        using var fps = new EtwFpsTracker();
        _fps = fps;
        if (settings.TrackFps && !fps.Start(tracker.Contains) && fps.Error is { } err) Status?.Invoke(err);

        // ---------------------------------------------------------------- RTSS: thermal guard + overlay
        var rtss = hardware is { RtssInstalled: true, RtssPath: { } rp } ? new RtssClient(rp) : null;
        // Make RTSS pick up profile edits made before launch (frame limit, limiter mode).
        rtss?.ReloadProfile(renderExe);
        // On a screen slower than the target (e.g. a 60 Hz TV), cap just under its refresh for this session.
        var capChanged = rtss is not null && EffectiveCap is { } ec && ec != game.FpsTarget && rtss.SetFramerateLimit(renderExe, ec);
        if (capChanged) Status?.Invoke($"Cap {EffectiveCap} FPS on this screen ({Display?.MaxRefreshHz} Hz)");
        var guard = settings.ThermalGuard.Enabled && game.ThermalGuard && Cap is { } cap && rtss is not null
            ? new ThermalGovernor(cap, settings.ThermalGuard, settings.CpuTempAlertC, settings.GpuTempAlertC)
            : null;
        lock (_capLock)
        {
            _rtss = rtss;
            _renderExe = renderExe;
            _guard = guard;
        }

        // ---------------------------------------------------------------- APU power limit
        var powerSet = false;
        if (RyzenAdj is not null && PowerWatts is { } watts)
        {
            var original = RyzenAdj.SaveOriginal();
            powerSet = RyzenAdj.Apply(watts, PowerMaxWatts);
            Status?.Invoke(powerSet ? $"Power limit {watts} W" + (original is not null ? $" (was {original.StapmW:0} W)" : "") : "Couldn't set the power limit (RyzenAdj failed)");
        }
        if (powerSet) lock (_capLock) _livePower = PowerWatts;

        // ---------------------------------------------------------------- Curve Optimizer undervolt
        if (RyzenAdj is not null && UndervoltOffset is { } uvOffset and < 0)
        {
            UndervoltApplied = RyzenAdj.ApplyUndervolt(uvOffset);
            Status?.Invoke(UndervoltApplied ? $"Undervolt {uvOffset} (Curve Optimizer)" : "Couldn't apply the undervolt; this APU or BIOS may not allow it");
            if (!UndervoltApplied) RyzenAdj.ClearCrashMarker();
        }
        if (settings.ThermalGuard.Enabled && game.ThermalGuard && rtss is null && game.FpsTarget is not null)
            Status?.Invoke("Thermal guard needs RTSS to change the cap live; only alerts are active.");
        var overlayStyle = settings.RtssOverlay ? settings.Overlay : OverlayStyle.Off;
        var overlay = overlayStyle != OverlayStyle.Off && rtss is not null;

        // ---------------------------------------------------------------- CPU pinning
        var affinity = CpuAffinityPlanner.Plan(cpuTopology, game.CorePinning);
        var pinned = new HashSet<int>();
        void Pin(IEnumerable<int> pids)
        {
            if (affinity is null) return;
            foreach (var p in pids.Where(pinned.Add))
            {
                try
                {
                    using var proc = Process.GetProcessById(p);
                    proc.ProcessorAffinity = (nint)(long)affinity.Mask;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Protected by anti-cheat or already gone; leave it alone.
                }
            }
        }
        Pin(tracker.Pids);
        if (affinity is not null) Status?.Invoke($"CPU: {affinity.Description}");

        // The slower Game Mode steps run while the game is loading.
        if (useGameMode)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            tracker.Refresh();
            Pin(tracker.Pids);
            foreach (var line in await gameMode.ActivatePostLaunchAsync(settings.GameMode, tuning, tracker.Pids, ct)) Status?.Invoke(line);
        }
        MemoryCleaner.TrimSelf();

        // ---------------------------------------------------------------- monitor loop
        double? maxCpu = null, maxGpu = null, sumCpu = 0, sumGpu = 0;
        double gpuLoadSum = 0;
        var gpuLoadSamples = 0;
        var cpuHistory = new Queue<ThermalSample>();
        var gpuHistory = new Queue<ThermalSample>();
        int cpuSamples = 0, gpuSamples = 0, alerts = 0, emptyPolls = 0, tick = 0;
        DateTime? cpuHotSince = null, gpuHotSince = null, lastCpuAlert = null, lastGpuAlert = null;
        var priorityRaised = new HashSet<int>(tracker.Pids);
        using var crashWatch = new CrashDetector.Watch();
        crashWatch.Track(tracker.Pids);
        var limiter = settings.BackgroundLimiter ? new BackgroundLimiter() : null;
        if (limiter is not null) limiter.Throttled += msg => Status?.Invoke(msg);
        var idleGov = settings.AdaptiveCap && rtss is not null ? new IdleCapGovernor(settings.AdaptiveIdleSeconds, settings.AdaptiveIdleCap) : null;
        var idleCapUsed = false;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                tick++;
                // Process list every 4 s; sensors every 2 s.
                if (tick % 2 == 0)
                {
                    var alive = tracker.Refresh();
                    var graceOver = DateTimeOffset.Now - start > TimeSpan.FromSeconds(30);
                    emptyPolls = alive == 0 ? emptyPolls + 1 : 0;
                    if (emptyPolls >= 2 && graceOver) break;
                    Pin(tracker.Pids);
                    crashWatch.Track(tracker.Pids);
                    limiter?.Tick(tracker.Pids);
                    if (useGameMode && settings.GameMode.RaiseGamePriority)
                    {
                        var fresh = tracker.Pids.Where(priorityRaised.Add).ToList();
                        if (fresh.Count > 0) GameModeController.RaiseGamePriority(fresh);
                    }
                }

                var r = temps.Read();
                if (r.CpuTempC is { } c) { maxCpu = Math.Max(maxCpu ?? 0, c); sumCpu += c; cpuSamples++; }
                if (r.GpuTempC is { } g) { maxGpu = Math.Max(maxGpu ?? 0, g); sumGpu += g; gpuSamples++; }
                if (r.GpuLoad is { } gl) { gpuLoadSum += gl; gpuLoadSamples++; }
                Push(cpuHistory, new ThermalSample(r.CpuTempC, r.CpuLoad, null));
                Push(gpuHistory, new ThermalSample(r.GpuTempC, r.GpuLoad, r.GpuPowerW));

                if (CheckSustained(r.CpuTempC, settings.CpuTempAlertC, ref cpuHotSince, ref lastCpuAlert))
                { alerts++; Alert?.Invoke(new TempAlert("CPU", r.CpuTempC!.Value, settings.CpuTempAlertC, ThermalDiagnosis.Explain("CPU", [.. cpuHistory], settings.CpuTempAlertC))); }
                if (CheckSustained(r.GpuTempC, settings.GpuTempAlertC, ref gpuHotSince, ref lastGpuAlert))
                { alerts++; Alert?.Invoke(new TempAlert("GPU", r.GpuTempC!.Value, settings.GpuTempAlertC, ThermalDiagnosis.Explain("GPU", [.. gpuHistory], settings.GpuTempAlertC))); }

                ThermalGovernor? currentGuard;
                lock (_capLock) currentGuard = _guard;
                if (currentGuard?.Update(r.CpuTempC, r.GpuTempC, DateTime.Now) is { } newCap && rtss!.SetFramerateLimit(renderExe, newCap))
                    Status?.Invoke(currentGuard.IsThrottling ? $"Thermal guard: running hot, cap lowered to {newCap} FPS" : $"Thermal guard: cooled down, cap back to {newCap} FPS");

                // Adaptive cap: no input for a while (menus, maps, cutscenes) → lower cap; any input → back.
                // The thermal guard wins while it's cooling things down.
                if (idleGov is not null && currentGuard is not { IsThrottling: true } && (currentGuard?.CurrentCap ?? Cap) is { } normalCap)
                {
                    var keys = InputIdle.SinceLastKeyboardOrMouse();
                    var pad = LastControllerInput is { } lci ? DateTime.UtcNow - lci() : TimeSpan.MaxValue;
                    var since = keys < pad ? keys : pad;
                    if (idleGov.Update(since, normalCap) is { } idleCap && rtss!.SetFramerateLimit(renderExe, idleCap))
                    {
                        idleCapUsed = true;
                        if (idleGov.Idle) Note($"Idle: cap {idleCap}");
                    }
                }

                // Vendor apps sometimes re-apply their own power limit; re-assert ours every minute.
                int? livePower;
                lock (_capLock) livePower = _livePower;
                if (powerSet && tick % 30 == 0 && livePower is { } lp) RyzenAdj!.Apply(lp, PowerMaxWatts);

                CheckBenchmark(r.GpuTempC);

                var recorder = fps.PrimaryRecorder;
                var p99 = recorder?.FrameTimePercentile(99) ?? 0;
                var live = new LiveStats(fps.CurrentFps, p99 > 0 ? 1000 / p99 : 0, r.CpuTempC, r.GpuTempC, r.GpuLoad,
                    DateTimeOffset.Now - start, currentGuard?.CurrentCap ?? Cap, currentGuard?.IsThrottling ?? false, BenchmarkSecondsLeft(),
                    DateTime.Now < _noteUntil ? _note : null);
                if (overlay) RtssClient.SetOsdText(_hud ? HudLine(live, livePower) : OverlayLine(live, overlayStyle));
                Live?.Invoke(live);
            }
        }
        catch (OperationCanceledException) { /* launcher closing */ }

        // ---------------------------------------------------------------- wrap up
        var end = DateTimeOffset.Now;
        CheckBenchmark(null, force: true);
        fps.Stop();
        _fps = null;
        if (overlay) RtssClient.ClearOsd();
        ThermalGovernor? endGuard;
        lock (_capLock) { endGuard = _guard; _rtss = null; }
        limiter?.RestoreAll();
        if (endGuard is { IsThrottling: true } || capChanged || _quickCap is not null || idleCapUsed)
        {
            // Put back what should stay: an in-game choice, the optimised cap, or no cap if none was set.
            var hadCap = game.AppliedChanges.Any(c => c.Action is RtssFrameLimitAction);
            rtss!.SetFramerateLimit(renderExe, _quickCap ?? (hadCap && game.FpsTarget is { } original ? original : 0));
        }
        if (powerSet) RyzenAdj!.Restore();
        // Clear the undervolt; the crash marker stays only if we never get here (freeze / power loss).
        if (UndervoltApplied) RyzenAdj!.ClearUndervolt();
        if (useGameMode)
        {
            foreach (var line in gameMode.Restore()) Status?.Invoke(line);
        }

        var finalRecorder = fps.PrimaryRecorder ?? new FrameTimeRecorder();
        var summary = finalRecorder.Summarise(game, start, end, Cap);
        summary.DisplayKey = Display?.Key;
        summary.DisplayWidth = Display?.Width ?? 0;
        summary.DisplayHeight = Display?.Height ?? 0;
        summary.RenderScale = RenderScale;
        summary.MaxCpuTempC = maxCpu;
        summary.MaxGpuTempC = maxGpu;
        summary.AvgCpuTempC = cpuSamples > 0 ? sumCpu / cpuSamples : null;
        summary.AvgGpuTempC = gpuSamples > 0 ? sumGpu / gpuSamples : null;
        summary.TempAlerts = alerts;
        summary.AvgGpuLoad = gpuLoadSamples > 0 ? gpuLoadSum / gpuLoadSamples : null;
        summary.PowerWatts = powerSet ? PowerWatts : null;
        summary.LimiterMode = LimiterMode;

        // Crash check: abnormal exit code, or Windows logged an "Application Error" for the game exe.
        var crashCode = crashWatch.CrashExitCode();
        var module = await Task.Run(() => CrashDetector.FaultingModule(renderExe, start)).ConfigureAwait(false);
        if (crashCode is not null || module is not null)
        {
            summary.Crashed = true;
            summary.FaultingModule = module ?? $"exit code 0x{crashCode:X8}";
            Status?.Invoke($"{game.Name} crashed ({summary.FaultingModule})");
        }

        store.AppendSession(summary);
        return summary;

        bool CheckSustained(double? value, double threshold, ref DateTime? hotSince, ref DateTime? lastAlert)
        {
            if (value is not { } v || v < threshold) { hotSince = null; return false; }
            hotSince ??= DateTime.Now;
            if ((DateTime.Now - hotSince.Value).TotalSeconds < settings.TempAlertSustainSeconds) return false;
            if (lastAlert is { } la && (DateTime.Now - la).TotalMinutes < 2) return false;
            lastAlert = DateTime.Now;
            return true;
        }
    }

    private static void Push(Queue<ThermalSample> q, ThermalSample s)
    {
        q.Enqueue(s);
        while (q.Count > 90) q.Dequeue(); // last ~3 minutes
    }

    private int? BenchmarkSecondsLeft()
    {
        lock (_benchLock) return _bench is null ? null : Math.Max(0, (int)(_benchEnd - DateTimeOffset.Now).TotalSeconds);
    }

    private void CheckBenchmark(double? gpuTemp, bool force = false)
    {
        FrameTimeRecorder? done;
        DateTimeOffset started;
        lock (_benchLock)
        {
            if (_bench is null) return;
            if (gpuTemp is { } g) _benchMaxGpu = Math.Max(_benchMaxGpu ?? 0, g);
            if (!force && DateTimeOffset.Now < _benchEnd) return;
            done = _bench;
            started = _benchStart;
            _bench = null;
            _fps?.SetSink(null);
        }
        var s = done.Summarise(game, started, DateTimeOffset.Now, Cap);
        if (!s.HasFpsData) { Status?.Invoke("Benchmark cancelled: not enough frames captured."); return; }
        s.MaxGpuTempC = _benchMaxGpu;
        var label = $"{Cap?.ToString() ?? "uncapped"} target · {(Display is { } d ? d.Label + " · " : "")}{RenderScale}% render · {game.AppliedChanges.Select(c => c.TweakId).Distinct().Count()} tweaks";
        var result = BenchmarkResult.From(s, label, game.AppliedChanges.Select(c => c.TweakId));
        store.AppendBenchmark(result);
        Native.MessageBeep(0x40);
        Status?.Invoke($"Benchmark done: avg {result.AverageFps:0}, 1% low {result.OnePercentLowFps:0}");
        BenchmarkCompleted?.Invoke(result);
    }

    /// <summary>Quick HUD line while View + LB is held.</summary>
    internal static string HudLine(LiveStats l, int? powerW)
    {
        var parts = new List<string> { $"{l.Fps:0} FPS", $"target {l.Cap?.ToString() ?? "none"}" };
        if (powerW is { } w) parts.Add($"power {w} W");
        if (l.GpuTempC is { } g) parts.Add($"GPU {g:0}°C");
        if (l.CpuTempC is { } c) parts.Add($"CPU {c:0}°C");
        parts.Add(powerW is null ? "D-pad up/down: target" : "D-pad up/down: target, left/right: power");
        if (l.Note is { } note) parts.Add(note);
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// Our RTSS line. Default: just current FPS and temperatures. Extra info only appears while it matters
    /// (thermal guard lowering the cap, a benchmark recording).
    /// </summary>
    internal static string OverlayLine(LiveStats l, OverlayStyle style)
    {
        var parts = new List<string>();
        if (l.Fps > 0) parts.Add($"{l.Fps:0} FPS");
        if (l.GpuTempC is { } g) parts.Add($"GPU {g:0}°C");
        if (l.CpuTempC is { } c) parts.Add($"CPU {c:0}°C");
        if (style == OverlayStyle.Detailed)
        {
            if (l.Cap is { } cap) parts.Add($"cap {cap}");
            if (l.OnePercentLow > 0) parts.Add($"1% low {l.OnePercentLow:0}");
        }
        if (l.ThermalThrottling && l.Cap is { } tc) parts.Add($"cooling: cap {tc}");
        if (l.Note is { } note) parts.Add(note);
        if (l.BenchmarkSecondsLeft is { } b) parts.Add($"benchmark {b}s");
        return string.Join("  ·  ", parts);
    }
}
