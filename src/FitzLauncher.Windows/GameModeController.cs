using System.Diagnostics;
using System.ServiceProcess;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

/// <summary>Everything Game Mode changed, persisted so it can be undone even after a crash.</summary>
public sealed class GameModeRestoreState
{
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public Guid? PreviousPowerScheme { get; set; }
    public Guid? PreviousOverlay { get; set; }
    public List<string> StoppedServices { get; set; } = [];
    public List<string> SuspendedProcessNames { get; set; } = [];
    public List<int> SuspendedPids { get; set; } = [];
    public Dictionary<int, ProcessPriorityClass> LoweredPriorities { get; set; } = [];
    public bool TimerResolutionSet { get; set; }
    /// <summary>Core parking minimum (AC index, percent) before we set it to 100; null if untouched.</summary>
    public int? PreviousCoreParkingMin { get; set; }
}

/// <summary>
/// The automatic Game Mode: power plan, RAM clear, background services/apps, priorities and the
/// timer. Every step is optional, tailored by <see cref="SystemAdvisor.Tune"/>, recorded in a
/// restore file before it's made, and undone when the game exits.
/// </summary>
public sealed class GameModeController(AppPaths paths, Action<string>? log = null)
{
    private GameModeRestoreState? _state;
    private CancellationTokenSource? _guardCts;

    public bool IsActive => _state is not null;

    /// <summary>Runs before the game starts: quick steps that help the game's first load.</summary>
    public List<string> ActivatePreLaunch(GameModeOptions o, GameModeTuning tuning, bool coreParkingOff = false)
    {
        var report = new List<string>();
        _state = new GameModeRestoreState();
        Persist();

        // Keep every core awake while playing (not on X3D: its scheduling relies on parking).
        if (coreParkingOff && !tuning.IsX3D)
        {
            var current = PowerPlans.GetCoreParkingMin();
            if (current is { } c && c < 100 && PowerPlans.SetCoreParkingMin(100))
            {
                _state.PreviousCoreParkingMin = c;
                Persist();
                report.Add("Core parking off (all cores awake)");
            }
        }

        if (o.SwitchPowerPlan && tuning.SwitchToHighPerformancePlan)
        {
            var (current, name) = PowerPlans.GetActive();
            var target = PowerPlans.EnsureHighPerformance();
            if (target is { } t && current != t && PowerPlans.SetActive(t))
            {
                _state.PreviousPowerScheme = current;
                report.Add($"Power plan: {name} → High performance");
            }
            else if (target is null && PowerPlans.GetOverlay() is { } overlay && overlay != PowerPlans.OverlayBestPerformance &&
                     PowerPlans.SetOverlay(PowerPlans.OverlayBestPerformance))
            {
                _state.PreviousOverlay = overlay;
                report.Add("Power mode → Best performance");
            }
            Persist();
        }
        else if (o.SwitchPowerPlan)
        {
            report.Add("Power plan left on Balanced (best for this CPU)");
        }

        if (o.CloseBackgroundApps)
        {
            var closed = 0;
            foreach (var name in o.CloseProcesses)
            foreach (var p in ByName(name))
            {
                using (p)
                {
                    try { p.Kill(); closed++; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
            if (closed > 0) report.Add($"Closed {closed} background updater(s)");

            var suspended = 0;
            foreach (var name in o.SuspendProcesses)
            foreach (var p in ByName(name))
            {
                using (p)
                {
                    var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, p.Id);
                    if (h == 0) continue;
                    if (Native.NtSuspendProcess(h) == 0)
                    {
                        _state.SuspendedPids.Add(p.Id);
                        if (!_state.SuspendedProcessNames.Contains(name)) _state.SuspendedProcessNames.Add(name);
                        suspended++;
                    }
                    Native.CloseHandle(h);
                }
            }
            if (suspended > 0) report.Add($"Paused {string.Join(", ", _state.SuspendedProcessNames.Select(Path.GetFileNameWithoutExtension))} (sync)");
            Persist();
        }

        if (o.ClearRam)
        {
            var before = MemoryCleaner.Snapshot();
            if (tuning.TrimBackgroundWorkingSets) MemoryCleaner.TrimBackgroundWorkingSets(new HashSet<int> { Environment.ProcessId });
            var freed = MemoryCleaner.PurgeStandbyList();
            var after = MemoryCleaner.Snapshot();
            report.Add($"RAM cleared: {Math.Max(freed, after.FreeMb - before.FreeMb) / 1024.0:0.0} GB freed ({after.FreeMb / 1024.0:0.0} GB free)");
        }

        if (o.HighResolutionTimer && tuning.UseTimerResolution)
        {
            // 0.5 ms system timer: steadier frame pacing for games that use Sleep() for their limiter (Windows 10).
            if (Native.NtSetTimerResolution(5000, true, out _) == 0)
            {
                _state.TimerResolutionSet = true;
                report.Add("Timer resolution: 0.5 ms");
            }
        }

        Persist();
        return report;
    }

    /// <summary>Runs once the game is up: slower steps (services) and per-process priorities.</summary>
    public async Task<List<string>> ActivatePostLaunchAsync(GameModeOptions o, GameModeTuning tuning, IReadOnlyCollection<int> gamePids, CancellationToken ct)
    {
        var report = new List<string>();
        if (_state is null) return report;

        if (o.StopServices)
        {
            var stops = o.Services.Select(name => Task.Run(() => StopService(name), ct)).ToList();
            var stopped = (await Task.WhenAll(stops)).Where(n => n is not null).Cast<string>().ToList();
            lock (_state) _state.StoppedServices.AddRange(stopped);
            Persist();
            if (stopped.Count > 0) report.Add($"Paused {stopped.Count} background service(s): updates, downloads, indexing, telemetry");
        }

        if (o.RaiseGamePriority) report.AddRange(RaiseGamePriority(gamePids));

        if (o.LowerBackgroundPriority)
        {
            var lowered = 0;
            foreach (var name in o.LowPriorityProcesses)
            foreach (var p in ByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (p.PriorityClass is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal)
                        {
                            _state.LoweredPriorities[p.Id] = p.PriorityClass;
                            p.PriorityClass = ProcessPriorityClass.BelowNormal;
                            lowered++;
                        }
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
            Persist();
            if (lowered > 0) report.Add($"Lowered priority of {lowered} background app process(es)");
        }

        if (o.StandbyListGuard && tuning.FreeMemoryThresholdMb > 0) StartStandbyGuard(tuning);
        return report;
    }

    public static List<string> RaiseGamePriority(IEnumerable<int> pids)
    {
        var report = new List<string>();
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.PriorityClass is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal)
                {
                    p.PriorityClass = ProcessPriorityClass.High;
                    report.Add($"{p.ProcessName}: priority High");
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return report;
    }

    /// <summary>ISLC-style: purge the standby list whenever free memory runs low during play.</summary>
    private void StartStandbyGuard(GameModeTuning tuning)
    {
        _guardCts = new CancellationTokenSource();
        var ct = _guardCts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var m = MemoryCleaner.Snapshot();
                if (m.FreeMb < tuning.FreeMemoryThresholdMb && m.StandbyMb > tuning.StandbyListMinimumMb)
                {
                    var freed = MemoryCleaner.PurgeStandbyList();
                    log?.Invoke($"Memory guard: purged standby list ({freed} MB)");
                }
            }
        }, ct).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private string? StopService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status != ServiceControllerStatus.Running || !sc.CanStop) return null;
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
            return name;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            log?.Invoke($"Service {name}: {e.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ restore

    public List<string> Restore()
    {
        _guardCts?.Cancel();
        _guardCts = null;
        var state = _state ?? Json.Load<GameModeRestoreState>(paths.GameModeState);
        _state = null;
        if (state is null) return [];
        var report = RestoreState(state);
        try { File.Delete(paths.GameModeState); } catch (IOException) { }
        return report;
    }

    /// <summary>Call at launcher start: undoes a Game Mode left active by a crash or power loss.</summary>
    public List<string> RecoverIfNeeded() => File.Exists(paths.GameModeState) ? Restore() : [];

    private List<string> RestoreState(GameModeRestoreState s)
    {
        var report = new List<string>();
        if (s.PreviousPowerScheme is { } plan && PowerPlans.SetActive(plan)) report.Add("Power plan restored");
        if (s.PreviousOverlay is { } overlay && PowerPlans.SetOverlay(overlay)) report.Add("Power mode restored");

        foreach (var pid in s.SuspendedPids)
        {
            var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
            if (h == 0) continue;
            Native.NtResumeProcess(h);
            Native.CloseHandle(h);
        }
        if (s.SuspendedPids.Count > 0) report.Add("Resumed paused apps");

        foreach (var (pid, priority) in s.LoweredPriorities)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.PriorityClass == ProcessPriorityClass.BelowNormal) p.PriorityClass = priority;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        if (s.TimerResolutionSet) Native.NtSetTimerResolution(5000, false, out _);
        if (s.PreviousCoreParkingMin is { } park && PowerPlans.SetCoreParkingMin(park)) report.Add("Core parking restored");

        // Restart services in the background; nothing is waiting on them.
        var services = s.StoppedServices.ToList();
        if (services.Count > 0)
        {
            _ = Task.Run(() =>
            {
                foreach (var name in services)
                {
                    try
                    {
                        using var sc = new ServiceController(name);
                        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { log?.Invoke($"Restart {name}: {e.Message}"); }
                }
            });
            report.Add($"Restarted {services.Count} service(s)");
        }
        return report;
    }

    private void Persist()
    {
        if (_state is null) return;
        try { Json.Save(paths.GameModeState, _state); }
        catch (IOException e) { log?.Invoke("Could not save Game Mode restore state: " + e.Message); }
    }

    private static Process[] ByName(string exeName) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName));
}
