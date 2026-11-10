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
    IReadOnlyList<LogicalProcessor> cpuTopology,
    AppPaths paths)
{
    public event Action<string>? Status;
    public event Action<LiveStats>? Live;
    public event Action<TempAlert>? Alert;
    public event Action<BenchmarkResult>? BenchmarkCompleted;
    /// <summary>Raised once the game process has started (used to start a trainer alongside it).</summary>
    public event Action? Launched;

    /// <summary>Record a benchmark automatically: after <c>Warmup</c> seconds, for <c>Seconds</c> (A/B tests).</summary>
    public (int Warmup, int Seconds)? AutoBenchmark { get; init; }
    private bool _autoBenchStarted;

    /// <summary>Why the session stuttered (set when it ends).</summary>
    public FitzLauncher.Core.Fps.StutterReport? StutterReport { get; private set; }
    private readonly List<FitzLauncher.Core.Fps.ContextSample> _samples = [];
    private readonly DiskActivity _disk = new();
    private DateTimeOffset _lastPowerChange = DateTimeOffset.MinValue;

    /// <summary>Changes the power limit and remembers when, so a stutter at that moment can be explained.</summary>
    private bool SetPower(int watts)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = RyzenAdj!.Apply(watts, PowerMaxWatts);
        if (ok) _lastPowerChange = DateTimeOffset.UtcNow;
        if (sw.ElapsedMilliseconds > 400) Trace?.Invoke($"RyzenAdj took {sw.ElapsedMilliseconds} ms to set {watts} W (slow runs can hitch the game)");
        return ok;
    }

    /// <summary>One-launch options from the display plan (e.g. Unity's -screen-width).</summary>
    public string ExtraArguments { get; init; } = "";
    /// <summary>Cap for the active screen; null = the game's own target.</summary>
    public int? EffectiveCap { get; init; }
    public DisplayInfo? Display { get; init; }
    public int RenderScale { get; init; } = 100;

    /// <summary>APU power limit for this session (watts), applied through RyzenAdj.</summary>
    public int? PowerWatts { get; init; }
    /// <summary>The power level that usually held the target last time (Auto TDP won't drop far below it, so a menu can't pull the limit down before the game needs it).</summary>
    public int? AutoTdpLearnedTypical { get; init; }
    public int PowerMaxWatts { get; init; } = 120;
    public int PowerMinWatts { get; init; } = 10;

    // ---- auto TDP (handhelds): off, observe (log only) or on
    public AutoTdpMode AutoTdp { get; init; }
    public int AutoTdpMinWatts { get; init; } = 25;
    public int AutoTdpMaxWatts { get; init; } = 60;
    /// <summary>What auto TDP learned this session (the range that held the target), for next time; null if too short.</summary>
    public AutoTdpProfile? AutoTdpLearned { get; private set; }
    /// <summary>The FPS target the learned range applies to.</summary>
    public int? AutoTdpTarget { get; private set; }
    /// <summary>A plain-English line for the end of an auto TDP session.</summary>
    public string? AutoTdpReport { get; private set; }
    /// <summary>Decisions and explanations, for the log (not shown on screen).</summary>
    public event Action<string>? Trace;
    /// <summary>The FPS Auto TDP holds when the game has no target of its own (your default target).</summary>
    public int AutoTdpFallbackTarget { get; init; } = 117;
    /// <summary>Set when Auto TDP was asked for but can't run (shown on screen and logged).</summary>
    public string? AutoTdpWhyNot { get; init; }
    /// <summary>What Auto TDP did at the start of this session, for Settings.</summary>
    public string? AutoTdpStartNote { get; private set; }
    private AutoTdpGovernor? _auto;
    private bool _autoSimulated;
    private StreamWriter? _autoCsv;
    private int _autoOverrides;
    private bool _autoWarned;
    private bool _osdReported;
    /// <summary>Find the lowest power limit that holds the target while playing.</summary>
    public bool LivePowerTuning { get; init; }
    /// <summary>Lowest power that held the target this session (live tuning), for next time.</summary>
    public int? LearnedWatts { get; private set; }
    /// <summary>The FPS target <see cref="LearnedWatts"/> applies to.</summary>
    public int? LearnedTarget { get; private set; }
    private PowerGovernor? _powerGov;
    public RyzenAdjClient? RyzenAdj { get; init; }
    /// <summary>RTSS limiter mode written for this session (0 async, 1 front-edge sync), for learning.</summary>
    public int? LimiterMode { get; init; }

    /// <summary>AMD Software features for this session (restored afterwards).</summary>
    public AmdProfile? Amd { get; init; }

    /// <summary>Cooling mode for this session (CPU boost, earlier thermal guard, guard trims power).</summary>
    public CoolingPlan? Cooling { get; init; }

    private ThermalGuardOptions GuardOptions => settings.ThermalGuard;
    /// <summary>The guard's start temperature: the setting, lowered by cooler modes.</summary>
    private double GuardTriggerC => settings.ThermalGuard.TriggerC - (Cooling?.GuardExtraMarginC ?? 0);

    /// <summary>Controller tuning for this session (virtual controller via ViGEmBus + HidHide), or null.</summary>
    public ControllerProfile? Controller { get; init; }

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
            if (_guard is not null) _guard = new ThermalGovernor(next, GuardOptions, GuardTriggerC);
            Note($"Target {next}");
            Status?.Invoke($"FPS target changed to {next} in-game");
        }
    }

    // ---------------------------------------------------------------- sleep / wake
    private readonly SleepPauseTracker _sleep = new();
    private GameFreezer? _freezer;
    private volatile IReadOnlyCollection<int> _gamePids = [];
    /// <summary>The game's processes right now (empty until it has started).</summary>
    public IReadOnlyCollection<int> GamePids => _gamePids;
    private nint _gameWindow;

    private void OnPowerEvent(PowerEvent e)
    {
        var pause = settings.PauseOnSleep && game.PauseOnSleep && _freezer is not null;
        switch (e)
        {
            case PowerEvent.DisplayOff or PowerEvent.Suspend when pause:
                if (!_sleep.TryPause(DateTimeOffset.Now)) return;
                var pids = _gamePids;
                _gameWindow = DesktopState.ForegroundOf(pids);
                var frozen = _freezer!.Freeze(pids); // synchronous: the system may sleep within a second or two
                Status?.Invoke(frozen > 0 ? "Device sleeping: game paused" : "Device sleeping: couldn't pause the game (protected process)");
                break;
            case PowerEvent.DisplayOn or PowerEvent.Resume when pause:
                if (_sleep.TryBeginResume()) _ = Task.Run(ResumeAfterWakeAsync);
                break;
            case PowerEvent.Resume:
                _ = Task.Run(async () => { await Task.Delay(1500); OnResume(); });
                break;
        }
    }

    /// <summary>Waits for sign-in (if the lock screen is up), lets the GPU and audio settle, then unpauses.</summary>
    private async Task ResumeAfterWakeAsync()
    {
        while (_sleep.IsResuming && !DesktopState.IsUnlocked()) await Task.Delay(500);
        await Task.Delay(1500);
        if (!_sleep.IsResuming) return; // went back to sleep while waiting
        _freezer!.Unfreeze();
        var slept = _sleep.CompleteResume(DateTimeOffset.Now);
        OnResume();
        DesktopState.BringBack(_gameWindow);
        Status?.Invoke(slept is { } d ? $"Woke up after {FormatSpan(d)}: game resumed" : "Game resumed");
    }

    private static string FormatSpan(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min" : $"{d.Seconds} s";

    private int? _livePower;
    private bool _hud;

    /// <summary>This session set the APU power limit (and puts it back after sleep itself).</summary>
    public bool OwnsPowerLimit { get { lock (_capLock) return _livePower is not null; } }

    /// <summary>Quick HUD (View + LB held): shows the target and power line until released.</summary>
    public void SetHud(bool open) => _hud = open;

    private string OsdMarker => Path.Combine(paths.Root, "rtss-osd-off.txt");

    private void MarkOsdOff(string exe)
    {
        try { File.WriteAllText(OsdMarker, exe); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void ClearOsdMarker()
    {
        try { File.Delete(OsdMarker); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>If a previous run was closed while the counters were hidden, RTSS's display for that game is still switched off: switch it back on.</summary>
    private void RestoreOsdLeftOff(RtssClient? rtss)
    {
        try
        {
            if (rtss is null || !File.Exists(OsdMarker)) return;
            var exe = File.ReadAllText(OsdMarker).Trim();
            if (exe.Length > 0 && rtss.SetOsdEnabled(exe, true)) Status?.Invoke("Switched RTSS's on-screen display back on (it was left off by an earlier session)");
            File.Delete(OsdMarker);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private volatile bool _overlayHidden;
    private bool _osdTurnedOff;

    /// <summary>Starts with the overlay hidden (remembered from PS + Right).</summary>
    public bool StartOverlayHidden { get; init; }

    /// <summary>
    /// PS + Right: hide every FPS counter (ours, and RTSS/Afterburner's for this game) or bring them back.
    /// FPS is still recorded. Returns the new state (true = hidden).
    /// </summary>
    public bool ToggleOverlayHidden()
    {
        _overlayHidden = !_overlayHidden;
        RtssClient? rtss;
        string? exe;
        lock (_capLock) { rtss = _rtss; exe = _renderExe; }
        if (_overlayHidden)
        {
            RtssClient.ClearOsd();
            if (rtss is not null && exe is not null)
            {
                MarkOsdOff(exe); // written first: if the launcher dies while hidden, the next start switches RTSS's display back on
                _osdTurnedOff = rtss.SetOsdEnabled(exe, false);
                if (!_osdTurnedOff) ClearOsdMarker();
            }
        }
        else
        {
            if (_osdTurnedOff && rtss is not null && exe is not null) rtss.SetOsdEnabled(exe, true);
            _osdTurnedOff = false;
            ClearOsdMarker();
            Note("Overlay on", 3);
        }
        Status?.Invoke(_overlayHidden ? "FPS counters hidden (still recording)" : "FPS counters shown");
        return _overlayHidden;
    }

    /// <summary>In-game power change, ±5 W per press (needs RyzenAdj).</summary>
    public void ChangePower(int direction)
    {
        lock (_capLock)
        {
            if (RyzenAdj is null || _livePower is not { } now) { Note("Power control needs RyzenAdj"); return; }
            var next = Math.Clamp(now + direction * 5, 10, PowerMaxWatts);
            if (next == now) { Note($"Power {now} W"); return; }
            if (!SetPower(next)) { Note("Couldn't change power"); return; }
            _livePower = next;
            _powerGov = null; // you're in charge now
            if (_auto is not null && !_autoSimulated) { _auto = null; Trace?.Invoke("Auto TDP paused: you changed the power limit by hand."); }
            LearnedWatts = null;
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

    // ---- auto TDP log: one CSV per session so decisions can be checked against how the game felt

    private void OpenAutoCsv()
    {
        try
        {
            var dir = Path.Combine(paths.Root, "auto-tdp");
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.EnumerateFiles(dir, "*.csv").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14))) File.Delete(old);
            var safe = string.Concat(game.Name.Where(c => char.IsLetterOrDigit(c) || c == ' ')).Trim().Replace(' ', '-');
            _autoCsv = new StreamWriter(Path.Combine(dir, $"{safe}-{DateTime.Now:yyyyMMdd-HHmm}.csv")) { AutoFlush = true };
            _autoCsv.WriteLine($"# {game.Name}, target {AutoTdpTarget} FPS, {(_autoSimulated ? "observe" : "on")}, range {AutoTdpMinWatts}-{AutoTdpMaxWatts} W");
            _autoCsv.WriteLine("seconds,limit_w,frames,fps,low1_fps,drawn_w,temp_c,thermal_hold,new_limit_w,note");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _autoCsv = null; }
    }

    private void WriteAutoCsv(DateTimeOffset start, int limit, AutoTdpSample s, AutoTdpDecision? d)
    {
        try
        {
            _autoCsv?.WriteLine(string.Join(',', ((DateTimeOffset.Now - start).TotalSeconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture), limit, s.Frames,
                s.Fps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), s.LowFps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                s.DrawnWatts?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "", s.TempC?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                s.ThermalHold ? 1 : 0, d?.Watts?.ToString() ?? "", d is null ? "" : "\"" + d.Reason.Replace("\"", "'") + "\""));
        }
        catch (IOException) { _autoCsv = null; }
    }

    private void FinishAutoTdp()
    {
        _autoCsv?.Dispose();
        _autoCsv = null;
        if (_auto is not { } g) return;
        AutoTdpLearned = _autoSimulated ? null : g.Summary();
        if (_autoSimulated)
        {
            AutoTdpReport = g.AverageDrawn is { } drawn
                ? $"Auto TDP (observed, nothing changed): it would have averaged {g.AverageLimit:0} W (up to {g.MaxUsed} W); the game drew {drawn:0} W on average, {g.PeakDrawn:0} W at most."
                : $"Auto TDP (observed): it would have averaged {g.AverageLimit:0} W, but the chip's power can't be read, so it couldn't tell how much the game really needs.";
        }
        else if (g.Summary() is { } p)
            AutoTdpReport = $"Auto TDP: {AutoTdpTarget} FPS held at {p.Min}–{p.Max} W (usually {p.Typical} W), {g.AverageLimit:0} W on average.";
        if (AutoTdpReport is not null) Trace?.Invoke(AutoTdpReport);
    }

    private void Note(string text, int seconds = 4)
    {
        _note = text;
        _noteUntil = DateTime.Now.AddSeconds(seconds);
    }

    /// <summary>A short line in the RTSS overlay (e.g. the trainer's hotkeys).</summary>
    public void ShowNote(string text, int seconds) => Note(text, seconds);

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

        // Cooling: CPU boost mode for the session (restored afterwards, or at the next start after a crash).
        var cooling = Cooling is { BoostMode: { } boost } ? new CoolingController(paths) : null;
        if (cooling is not null && cooling.ApplyBoost(Cooling!.BoostMode!.Value))
            Status?.Invoke(Cooling.BoostMode == 0 ? "Cooling: CPU boost off" : "Cooling: CPU boost set to efficient");

        // AMD features (Anti-Lag, Chill, RSR, AFMF…): global driver settings, applied now and restored afterwards.
        var amdApplied = false;
        if (Amd is { Any: true } amdProfile)
        {
            var line = await Task.Run(() => AmdFeatures.ApplyForSession(paths, amdProfile), ct);
            amdApplied = line?.StartsWith("AMD: ", StringComparison.Ordinal) == true;
            if (line is not null) Status?.Invoke(line);
        }

        // Controller tuning starts before the game, so the game only ever sees the tuned virtual pad.
        using var remapper = Controller is { Enabled: true } cp ? new ControllerRemapper(cp, paths, line => Status?.Invoke(line)) : null;
        remapper?.Start();

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
        Launched?.Invoke();

        var tracker = new GameProcessTracker(pid, installDir);
        tracker.Refresh();
        _gamePids = [.. tracker.Pids];

        // ---------------------------------------------------------------- sleep / wake
        _freezer = new GameFreezer(paths);
        using var power = new PowerEvents();
        power.Changed += OnPowerEvent;
        var overridden = settings.IdleSleepInGame
            && PowerRequestOverride.Set(paths, new[] { Path.GetFileName(game.ExePath), renderExe });
        if (settings.IdleSleepInGame && !overridden) Status?.Invoke("Couldn't stop the game holding the screen on (needs the launcher to run as admin).");
        if (settings.PauseOnSleep && !power.Working) Status?.Invoke("Sleep notifications unavailable; pause-on-sleep is off for this session.");
        using var fps = new EtwFpsTracker();
        _fps = fps;
        if (settings.TrackFps && !fps.Start(tracker.Contains) && fps.Error is { } err) Status?.Invoke(err);

        // ---------------------------------------------------------------- RTSS: thermal guard + overlay
        var rtss = hardware is { RtssInstalled: true, RtssPath: { } rp } ? new RtssClient(rp) : null;
        // Make RTSS pick up profile edits made before launch (frame limit, limiter mode).
        rtss?.ReloadProfile(renderExe);
        // On a screen slower than the target (e.g. a 60 Hz TV), cap just under its refresh for this session.
        var capChanged = rtss is not null && EffectiveCap is { } ec && ec != game.FpsTarget && rtss.SetFramerateLimit(renderExe, ec);
        if (capChanged) Status?.Invoke(EffectiveCap == 0 ? "No FPS cap on this screen" : $"Cap {EffectiveCap} FPS on this screen ({Display?.MaxRefreshHz} Hz)");
        var guard = settings.ThermalGuard.Enabled && game.ThermalGuard && Cap is { } cap and > 0 && rtss is not null
            ? new ThermalGovernor(cap, GuardOptions, GuardTriggerC)
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
            if (!powerSet) Trace?.Invoke($"RyzenAdj could not set {watts} W: {RyzenAdj.LastError}");
            Status?.Invoke(powerSet ? $"Power limit {watts} W" + (original is not null ? $" (was {original.StapmW:0} W)" : "") : "Couldn't set the power limit (RyzenAdj failed)");
        }
        if (powerSet) lock (_capLock) _livePower = PowerWatts;
        if (AutoTdpWhyNot is { } blocked)
        {
            AutoTdpStartNote = $"Auto TDP isn't running: {blocked}.";
            Status?.Invoke(AutoTdpStartNote);
            Note(AutoTdpStartNote, 12);
        }
        else if (AutoTdp == AutoTdpMode.On && !powerSet)
        {
            AutoTdpStartNote = "Auto TDP isn't running: RyzenAdj couldn't set the power limit on this device (Settings → Power limit → Test). If OneXConsole locks the limit, turn off its TDP control." + (RyzenAdj.LastError.Length > 0 ? $" RyzenAdj said: {RyzenAdj.LastError}" : "");
            Status?.Invoke(AutoTdpStartNote);
            Note("Auto TDP isn't running: couldn't set the power limit", 12);
        }
        if (AutoTdpWhyNot is null && AutoTdp != AutoTdpMode.Off && (powerSet || AutoTdp == AutoTdpMode.Observe) && (Cap is > 0 ? Cap : AutoTdpFallbackTarget) is { } autoTarget and > 0)
        {
            if (Cap is not > 0) Status?.Invoke($"Auto TDP: this game has no FPS target, so it holds your default ({autoTarget} FPS) or better");
            _autoSimulated = AutoTdp == AutoTdpMode.Observe;
            var startWatts = _autoSimulated ? (int)Math.Round(RyzenAdj?.Read()?.StapmW ?? 45) : PowerWatts!.Value;
            _auto = new AutoTdpGovernor(autoTarget, AutoTdpMinWatts, AutoTdpMaxWatts, startWatts, simulated: _autoSimulated, thermalTriggerC: GuardTriggerC, learnedTypical: AutoTdpLearnedTypical);
            AutoTdpTarget = autoTarget;
            AutoTdpStartNote = _autoSimulated
                ? $"Auto TDP is watching (not changing anything): it logs the power it would use to hold {autoTarget} FPS"
                : $"Auto TDP on: holding {autoTarget} FPS with the least power ({AutoTdpMinWatts}–{AutoTdpMaxWatts} W), starting at {_auto.Current} W";
            Status?.Invoke(AutoTdpStartNote);
            OpenAutoCsv();
        }
        else if (powerSet && LivePowerTuning && Cap is > 0 and { } powerTarget)
        {
            _powerGov = new PowerGovernor(PowerWatts!.Value, PowerMinWatts, PowerMaxWatts, powerTarget);
            LearnedTarget = powerTarget;
            Status?.Invoke($"Power: finding the lowest limit that holds {powerTarget} FPS (starting at {PowerWatts} W)");
        }

        // ---------------------------------------------------------------- Curve Optimizer undervolt
        if (RyzenAdj is not null && UndervoltOffset is { } uvOffset and < 0)
        {
            UndervoltApplied = RyzenAdj.ApplyUndervolt(uvOffset);
            Status?.Invoke(UndervoltApplied ? $"Undervolt {uvOffset} (Curve Optimizer)" : "Couldn't apply the undervolt; this APU or BIOS may not allow it");
            if (!UndervoltApplied) RyzenAdj.ClearCrashMarker();
        }
        if (settings.ThermalGuard.Enabled && game.ThermalGuard && rtss is null && game.FpsTarget is not null)
            Status?.Invoke("Thermal guard needs RTSS to change the cap live; only alerts are active.");
        var overlayLayout = game.Overlay ?? settings.EffectiveOverlay;
        var overlay = overlayLayout.Enabled && rtss is not null;
        RestoreOsdLeftOff(rtss);
        if (StartOverlayHidden && rtss is not null) ToggleOverlayHidden();

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
                if (LowPowerSleep.Asleep) continue; // nothing to watch while the device sleeps; keeps the chip idle
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
                    _gamePids = [.. tracker.Pids];
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
                if (_samples.Count < 20_000)
                {
                    var top = limiter?.LastTop ?? (null, 0);
                    _samples.Add(new FitzLauncher.Core.Fps.ContextSample(DateTimeOffset.UtcNow, r.CpuTempC, r.GpuTempC, r.CpuLoad, r.GpuLoad,
                        MemoryCleaner.FreePercent(), top.Name, top.Cpu, _guard?.IsThrottling == true,
                        _disk.BusyPercent(), (DateTimeOffset.UtcNow - _lastPowerChange).TotalSeconds < 4));
                }

                if (CheckSustained(r.CpuTempC, settings.CpuTempAlertC, ref cpuHotSince, ref lastCpuAlert))
                { alerts++; Alert?.Invoke(new TempAlert("CPU", r.CpuTempC!.Value, settings.CpuTempAlertC, ThermalDiagnosis.Explain("CPU", [.. cpuHistory], settings.CpuTempAlertC))); }
                if (CheckSustained(r.GpuTempC, settings.GpuTempAlertC, ref gpuHotSince, ref lastGpuAlert))
                { alerts++; Alert?.Invoke(new TempAlert("GPU", r.GpuTempC!.Value, settings.GpuTempAlertC, ThermalDiagnosis.Explain("GPU", [.. gpuHistory], settings.GpuTempAlertC))); }

                ThermalGovernor? currentGuard;
                lock (_capLock) currentGuard = _guard;
                // While Auto TDP can still cool the chip by lowering power, the FPS-cap guard waits (unless it's very hot).
                var hotNow = Math.Max(r.CpuTempC ?? 0, r.GpuTempC ?? 0);
                var guardMayStepDown = _auto is not { CanCoolByPower: true } || hotNow >= GuardTriggerC + 6;
                if (currentGuard?.Update(r.CpuTempC, r.GpuTempC, DateTime.Now, guardMayStepDown) is { } newCap && rtss!.SetFramerateLimit(renderExe, newCap))
                {
                    Status?.Invoke(currentGuard.IsThrottling ? $"Thermal guard: running hot, cap lowered to {newCap} FPS" : $"Thermal guard: cooled down, cap back to {newCap} FPS");
                    // Cooler modes also trim the APU's power while hot, and give it back once cool.
                    if (Cooling is { GuardLowersPower: true } && powerSet)
                        lock (_capLock)
                            if (_livePower is { } lpw)
                            {
                                var nextW = currentGuard.IsThrottling ? Math.Max(10, lpw - 5) : Math.Min(PowerWatts!.Value, lpw + 5);
                                if (nextW != lpw && SetPower(nextW)) _livePower = nextW;
                            }
                }

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

                // Auto TDP: raise fast when frames dip at the limit, creep down while the target is held with room to spare.
                if (_auto is { } ag && idleGov is not { Idle: true } && BenchmarkSecondsLeft() is null)
                {
                    var (frames, avg, low) = fps.PrimaryRecorder?.RecentStats(6) ?? (0, 0, 0);
                    var hot = Math.Max(r.CpuTempC ?? 0, r.GpuTempC ?? 0);
                    var sample = new AutoTdpSample(frames, avg, low, r.Watts, hot > 0 ? hot : null, currentGuard?.IsThrottling == true);
                    var before = ag.Current;
                    if ((currentGuard?.CurrentCap ?? Cap) is { } heldTo and > 0) ag.SetTarget(heldTo); // the target it's holding right now
                    var decision = ag.Update(sample, DateTime.UtcNow);
                    WriteAutoCsv(start, before, sample, decision);
                    if (decision is { } dec)
                    {
                        Trace?.Invoke("Auto TDP" + (_autoSimulated ? " (observing, not applied)" : "") + ": " + dec.Reason);
                        if (!_autoSimulated && dec.Watts is { } nw && SetPower(nw))
                        {
                            lock (_capLock) _livePower = nw;
                            Note($"TDP {nw} W");
                        }
                    }
                }

                // Live power tuning: the governor follows the target unless the thermal guard or idle cap is in charge.
                if (_powerGov is { } gov && currentGuard is not { IsThrottling: true } && idleGov is not { Idle: true } && BenchmarkSecondsLeft() is null)
                {
                    int? targetNow;
                    lock (_capLock) targetNow = _livePower;
                    if (gov.Update(fps.CurrentFps, DateTime.UtcNow) is { } w && w != targetNow && SetPower(w))
                    {
                        lock (_capLock) _livePower = w;
                        Note($"Power {w} W");
                    }
                    LearnedWatts = gov.Learned;
                }

                // Vendor apps sometimes re-apply their own power limit; re-assert ours every minute.
                int? livePower;
                lock (_capLock) livePower = _livePower;
                if (_auto is not null && !_autoSimulated && powerSet && tick % 30 == 0 && livePower is { } al)
                {
                    // Auto TDP changes the limit often, so check about once a minute (reading the chip is itself a hit) that nothing else (OneXConsole's own per-game
                    // TDP) has changed it back; put ours back, and say so once if it keeps happening.
                    if (RyzenAdj!.Read() is { } actual && Math.Abs(actual.StapmW - al) > 1.5)
                    {
                        SetPower(al);
                        if (++_autoOverrides >= 2 && !_autoWarned)
                        {
                            _autoWarned = true;
                            Status?.Invoke("Something else keeps changing the power limit (probably OneXConsole's per-game TDP). Turn that off for this game so Auto TDP stays in charge.");
                        }
                    }
                }
                else if (powerSet && tick % 30 == 0 && livePower is { } lp) RyzenAdj!.Apply(lp, PowerMaxWatts);

                if (AutoBenchmark is { } ab && !_autoBenchStarted && DateTimeOffset.Now - start >= TimeSpan.FromSeconds(ab.Warmup) - _sleep.Asleep)
                {
                    _autoBenchStarted = StartBenchmark(ab.Seconds);
                    if (_autoBenchStarted) Note($"A/B test: recording {ab.Seconds}s", 6);
                }
                CheckBenchmark(r.GpuTempC);

                var recorder = fps.PrimaryRecorder;
                var p99 = recorder?.FrameTimePercentile(99) ?? 0;
                var live = new LiveStats(fps.CurrentFps, p99 > 0 ? 1000 / p99 : 0, r.CpuTempC, r.GpuTempC, r.GpuLoad,
                    DateTimeOffset.Now - start, currentGuard?.CurrentCap ?? Cap, currentGuard?.IsThrottling ?? false, BenchmarkSecondsLeft(),
                    DateTime.Now < _noteUntil ? _note : null);
                if (overlay && (!_overlayHidden || _hud))
                {
                    var other = overlayLayout.AvoidDuplicates ? OsdTextAnalyzer.Analyze(RtssClient.ReadOtherOsdText()) : (OtherOsd?)null;
                    RtssClient.SetOsdText(_hud ? HudLine(live, livePower) : OverlayFormatter.Format(overlayLayout,
                        new OverlayValues(live.Fps, live.OnePercentLow, r.Watts, r.CpuTempC, r.GpuTempC, r.GpuLoad, live.Cap, live.ThermalThrottling,
                            live.BenchmarkSecondsLeft, live.Note, DateTime.Now, other, _auto?.Current, _autoSimulated)));
                    if (other is { } seen && !_osdReported && (seen.ShowsFps || seen.ShowsPower || seen.Temperatures > 0))
                    {
                        _osdReported = true;
                        Trace?.Invoke($"Overlay: another on-screen display is showing {(seen.ShowsFps ? "FPS, " : "")}{(seen.ShowsPower ? "power, " : "")}{seen.Temperatures} temperature(s); Fitz leaves those out");
                    }
                }
                Live?.Invoke(live);
            }
        }
        catch (OperationCanceledException) { /* launcher closing */ }

        // ---------------------------------------------------------------- wrap up
        power.Changed -= OnPowerEvent;
        if (_sleep.IsPaused) { _freezer.Unfreeze(); _sleep.CompleteResume(DateTimeOffset.Now); }
        if (settings.IdleSleepInGame) PowerRequestOverride.Clear(paths);
        var end = DateTimeOffset.Now;
        CheckBenchmark(null, force: true);
        fps.Stop();
        _fps = null;
        if (overlay) RtssClient.ClearOsd();
        if (_osdTurnedOff && rtss is not null) { rtss.SetOsdEnabled(renderExe, true); _osdTurnedOff = false; } // never leave RTSS's OSD off
        ClearOsdMarker();
        ThermalGovernor? endGuard;
        lock (_capLock) { endGuard = _guard; _rtss = null; }
        limiter?.RestoreAll();
        if (endGuard is { IsThrottling: true } || capChanged || _quickCap is not null || idleCapUsed)
        {
            // Put back what should stay: an in-game choice, the optimised cap, or no cap if none was set.
            var hadCap = game.AppliedChanges.Any(c => c.Action is RtssFrameLimitAction);
            rtss!.SetFramerateLimit(renderExe, _quickCap ?? (hadCap && game.FpsTarget is { } original ? original : 0));
        }
        FinishAutoTdp();
        if (powerSet) RyzenAdj!.Restore();
        cooling?.Restore();
        if (amdApplied) await Task.Run(() => AmdFeatures.RestoreAfterSession(paths));
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
        summary.PowerWatts = _auto is { } done && !_autoSimulated ? (int)Math.Round(done.AverageLimit) : powerSet ? LearnedWatts ?? PowerWatts : null; // the lowest that held, when tuned live
        summary.LimiterMode = LimiterMode;
        summary.AsleepSeconds = Math.Round(_sleep.Asleep.TotalSeconds);

        // Stutter detective: match each spike with what the PC was doing at that moment.
        if (summary.HasFpsData && fps.PrimaryRecorder is { } rec)
        {
            StutterReport = FitzLauncher.Core.Fps.StutterDetective.Analyze(game, start.ToUniversalTime(), end.ToUniversalTime(), rec.Stutters, _samples, settings.CpuTempAlertC, settings.GpuTempAlertC);
            try { Json.Save(Path.Combine(paths.Root, "stutters", $"{game.Id:N}.json"), StutterReport); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

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

}
