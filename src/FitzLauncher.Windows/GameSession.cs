using System.Diagnostics;
using FitzLauncher.Core.Fps;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

public readonly record struct LiveStats(double Fps, double OnePercentLow, double? CpuTempC, double? GpuTempC, double? GpuLoad, TimeSpan Elapsed, int? Cap, bool ThermalThrottling, int? BenchmarkSecondsLeft);
public readonly record struct TempAlert(string Component, double TempC, double ThresholdC);

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
            foreach (var line in await Task.Run(() => gameMode.ActivatePreLaunch(settings.GameMode, tuning), ct)) Status?.Invoke(line);
        }

        var start = DateTimeOffset.Now;
        int pid;
        try
        {
            var args = $"{game.Arguments} {game.OptimizerArguments}".Trim();
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
        var guard = settings.ThermalGuard.Enabled && game.ThermalGuard && game.FpsTarget is { } cap && rtss is not null
            ? new ThermalGovernor(cap, settings.ThermalGuard, settings.CpuTempAlertC, settings.GpuTempAlertC)
            : null;
        if (settings.ThermalGuard.Enabled && game.ThermalGuard && rtss is null && game.FpsTarget is not null)
            Status?.Invoke("Thermal guard needs RTSS to change the cap live; only alerts are active.");
        var overlay = settings.RtssOverlay && rtss is not null;

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
        int cpuSamples = 0, gpuSamples = 0, alerts = 0, emptyPolls = 0, tick = 0;
        DateTime? cpuHotSince = null, gpuHotSince = null, lastCpuAlert = null, lastGpuAlert = null;
        var priorityRaised = new HashSet<int>(tracker.Pids);

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
                    if (useGameMode && settings.GameMode.RaiseGamePriority)
                    {
                        var fresh = tracker.Pids.Where(priorityRaised.Add).ToList();
                        if (fresh.Count > 0) GameModeController.RaiseGamePriority(fresh);
                    }
                }

                var r = temps.Read();
                if (r.CpuTempC is { } c) { maxCpu = Math.Max(maxCpu ?? 0, c); sumCpu += c; cpuSamples++; }
                if (r.GpuTempC is { } g) { maxGpu = Math.Max(maxGpu ?? 0, g); sumGpu += g; gpuSamples++; }

                if (CheckSustained(r.CpuTempC, settings.CpuTempAlertC, ref cpuHotSince, ref lastCpuAlert))
                { alerts++; Alert?.Invoke(new TempAlert("CPU", r.CpuTempC!.Value, settings.CpuTempAlertC)); }
                if (CheckSustained(r.GpuTempC, settings.GpuTempAlertC, ref gpuHotSince, ref lastGpuAlert))
                { alerts++; Alert?.Invoke(new TempAlert("GPU", r.GpuTempC!.Value, settings.GpuTempAlertC)); }

                if (guard?.Update(r.CpuTempC, r.GpuTempC, DateTime.Now) is { } newCap && rtss!.SetFramerateLimit(renderExe, newCap))
                    Status?.Invoke(guard.IsThrottling ? $"Thermal guard: running hot, cap lowered to {newCap} FPS" : $"Thermal guard: cooled down, cap back to {newCap} FPS");

                CheckBenchmark(r.GpuTempC);

                var recorder = fps.PrimaryRecorder;
                var p99 = recorder?.FrameTimePercentile(99) ?? 0;
                var live = new LiveStats(fps.CurrentFps, p99 > 0 ? 1000 / p99 : 0, r.CpuTempC, r.GpuTempC, r.GpuLoad,
                    DateTimeOffset.Now - start, guard?.CurrentCap ?? game.FpsTarget, guard?.IsThrottling ?? false, BenchmarkSecondsLeft());
                if (overlay) RtssClient.SetOsdText(OverlayLine(live));
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
        if (guard is { IsThrottling: true })
        {
            // Put back exactly what was there: the optimised cap, or no cap if Optimise never set one.
            var hadCap = game.AppliedChanges.Any(c => c.Action is RtssFrameLimitAction);
            rtss!.SetFramerateLimit(renderExe, hadCap ? guard.OriginalCap : 0);
        }
        if (useGameMode)
        {
            foreach (var line in gameMode.Restore()) Status?.Invoke(line);
        }

        var finalRecorder = fps.PrimaryRecorder ?? new FrameTimeRecorder();
        var summary = finalRecorder.Summarise(game, start, end, game.FpsTarget);
        summary.MaxCpuTempC = maxCpu;
        summary.MaxGpuTempC = maxGpu;
        summary.AvgCpuTempC = cpuSamples > 0 ? sumCpu / cpuSamples : null;
        summary.AvgGpuTempC = gpuSamples > 0 ? sumGpu / gpuSamples : null;
        summary.TempAlerts = alerts;

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
        var s = done.Summarise(game, started, DateTimeOffset.Now, game.FpsTarget);
        if (!s.HasFpsData) { Status?.Invoke("Benchmark cancelled: not enough frames captured."); return; }
        s.MaxGpuTempC = _benchMaxGpu;
        var label = $"{game.FpsTarget?.ToString() ?? "uncapped"} target · {game.AppliedChanges.Select(c => c.TweakId).Distinct().Count()} tweaks";
        var result = BenchmarkResult.From(s, label, game.AppliedChanges.Select(c => c.TweakId));
        store.AppendBenchmark(result);
        Native.MessageBeep(0x40);
        Status?.Invoke($"Benchmark done: avg {result.AverageFps:0}, 1% low {result.OnePercentLowFps:0}");
        BenchmarkCompleted?.Invoke(result);
    }

    /// <summary>One compact line for RTSS. Afterburner already shows FPS/frametime, so this adds what it can't.</summary>
    private static string OverlayLine(LiveStats l)
    {
        var parts = new List<string> { "FITZ" };
        if (l.Cap is { } cap) parts.Add(l.ThermalThrottling ? $"cap {cap} (thermal)" : $"cap {cap}");
        if (l.OnePercentLow > 0) parts.Add($"1% low {l.OnePercentLow:0}");
        if (l.GpuTempC is { } g) parts.Add($"GPU {g:0}C");
        if (l.CpuTempC is { } c) parts.Add($"CPU {c:0}C");
        if (l.BenchmarkSecondsLeft is { } b) parts.Add($"BENCH {b}s");
        return string.Join(" | ", parts);
    }
}
