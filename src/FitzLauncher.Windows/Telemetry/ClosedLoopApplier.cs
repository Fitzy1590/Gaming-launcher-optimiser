using System.ComponentModel;
using System.Diagnostics;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Storage;
using FitzLauncher.Core.Telemetry;

namespace FitzLauncher.Windows;

/// <summary>What the closed-loop tuner changed, written before each change so a crash can be undone at the next start.</summary>
public sealed class ClosedLoopRestoreState
{
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public List<PriorityEntry> Priorities { get; set; } = [];
    public List<AffinityEntry> Affinities { get; set; } = [];
    public List<PowerEntry> Power { get; set; } = [];
    public List<CapEntry> Caps { get; set; } = [];
    public List<HeldBackEntry> HeldBack { get; set; } = [];

    public sealed class PriorityEntry { public TunerRule Rule { get; set; } public int Pid { get; set; } public string Name { get; set; } = ""; public ProcessPriorityClass Previous { get; set; } }
    public sealed class AffinityEntry { public TunerRule Rule { get; set; } public int Pid { get; set; } public string Name { get; set; } = ""; public long PreviousMask { get; set; } }
    public sealed class PowerEntry { public TunerRule Rule { get; set; } public Guid? PreviousScheme { get; set; } public Guid? PreviousOverlay { get; set; } }
    /// <summary>The frame cap before the tuner's (0 = none). RtssPath and RenderExe let start-up recovery put it back.</summary>
    public sealed class CapEntry { public TunerRule Rule { get; set; } public int PreviousFps { get; set; } public string RtssPath { get; set; } = ""; public string RenderExe { get; set; } = ""; }
    public sealed class HeldBackEntry { public TunerRule Rule { get; set; } public string Name { get; set; } = ""; }
}

/// <summary>What the applier needs from the running session. All delegates must be quick and must not throw.</summary>
public sealed class ClosedLoopHooks
{
    public required Func<IReadOnlyCollection<int>> GamePids { get; init; }
    public required Func<TunerContext> Context { get; init; }
    /// <summary>The latest CPU and GPU temperatures (null when no sensor can tell).</summary>
    public required Func<(double? Cpu, double? Gpu)> Temperatures { get; init; }
    /// <summary>The frame cap in force (null or 0 = none), and a way to change it through RTSS.</summary>
    public required Func<int?> GetCap { get; init; }
    public required Func<int, bool> SetCap { get; init; }
    /// <summary>Background apps right now, busiest first or not: the applier chooses.</summary>
    public required Func<IReadOnlyList<AppUsage>> BackgroundApps { get; init; }
    public string RtssPath { get; init; } = "";
    public string RenderExe { get; init; } = "";
    public Action<TempAlert>? Toast { get; init; }
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Applies the tuner's decisions through the classes that already own each setting: <see cref="GameModeController.RaiseGamePriority"/>,
/// <see cref="CpuAffinityPlanner"/>, <see cref="ResourceApplier"/>, <see cref="MemoryCleaner"/>, <see cref="PowerPlans"/> and RTSS. No second path to the same setting.
/// Every change is recorded in <c>closed-loop-restore.json</c> first. Nothing here touches a game's memory or injects anything.
/// </summary>
public sealed class ClosedLoopApplier(AppPaths paths, ClosedLoopHooks hooks, ResourceApplier resources, IReadOnlyList<LogicalProcessor> topology)
{
    /// <summary>Background apps held back at once, and the least CPU an app must use to be worth holding back.</summary>
    private const int MaxHeldBackApps = 3;
    private const double MinBackgroundCpuPercent = 1;

    private readonly ClosedLoopRestoreState _state = new();
    private readonly Lock _lock = new();

    /// <summary>Carries out one decision. Returns false when nothing could be changed (so the log can say so).</summary>
    public bool Apply(TunerAction a)
    {
        if (a.Rule is not { } rule) return false;
        switch (a.Type)
        {
            case TunerActionType.Release or TunerActionType.Undo:
                return Revert(rule);
            case TunerActionType.Apply or TunerActionType.Advise:
                var changed = false;
                if (a.Effects.HasFlag(TunerEffect.HighPriority)) changed |= RaisePriority(rule);
                if (a.Effects.HasFlag(TunerEffect.PCoreAffinity)) changed |= PinCores(rule);
                if (a.Effects.HasFlag(TunerEffect.HoldBackBackground)) changed |= HoldBack(rule);
                if (a.Effects.HasFlag(TunerEffect.RaisePowerProfile)) changed |= RaisePower(rule);
                if (a.Effects.HasFlag(TunerEffect.StockPowerProfile)) changed |= StockPower(rule);
                if (a.Effects.HasFlag(TunerEffect.FrameCap) && a.FrameCapFps is { } cap) changed |= SetFrameCap(rule, cap);
                if (a.Effects.HasFlag(TunerEffect.TrimWorkingSets)) changed |= Trim();
                if (a.Effects.HasFlag(TunerEffect.Toast)) { Toast(a.Text); changed = true; }
                if (a.Effects.HasFlag(TunerEffect.AdviseHags)) changed = true; // advice only: the text is in the log
                return changed;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ apply

    private bool RaisePriority(TunerRule rule)
    {
        var any = false;
        foreach (var pid in hooks.GamePids())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.PriorityClass is not (ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal)) continue;
                lock (_lock)
                {
                    _state.Priorities.Add(new() { Rule = rule, Pid = pid, Name = p.ProcessName, Previous = p.PriorityClass });
                    Persist(); // record first, so a crash mid-change still restores
                }
                GameModeController.RaiseGamePriority([pid]);
                any = true;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        return any;
    }

    private bool PinCores(TunerRule rule)
    {
        var plan = CpuAffinityPlanner.Plan(topology, CorePinning.Auto);
        if (plan is null) { hooks.Log?.Invoke("Core pinning isn't needed on this processor, so it was left alone."); return false; }
        var any = false;
        foreach (var pid in hooks.GamePids())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var before = (long)p.ProcessorAffinity;
                if (before == (long)plan.Mask) continue;
                lock (_lock)
                {
                    _state.Affinities.Add(new() { Rule = rule, Pid = pid, Name = p.ProcessName, PreviousMask = before });
                    Persist();
                }
                p.ProcessorAffinity = (nint)(long)plan.Mask;
                any = true;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        return any;
    }

    private bool HoldBack(TunerRule rule)
    {
        var apps = hooks.BackgroundApps()
            .Where(a => AppClassifier.MayTouch(a.Name) && a.CpuPercent >= MinBackgroundCpuPercent)
            .OrderByDescending(a => a.CpuPercent).Take(MaxHeldBackApps).ToList();
        var any = false;
        foreach (var app in apps)
        {
            lock (_lock)
            {
                _state.HeldBack.Add(new() { Rule = rule, Name = app.Name });
                Persist();
            }
            if (resources.Apply(app, FreeAction.Restrain) > 0) any = true;
        }
        return any;
    }

    private bool RaisePower(TunerRule rule)
    {
        var (current, _) = PowerPlans.GetActive();
        var overlay = PowerPlans.GetOverlay();
        // Look for an existing fast plan; never create one here, because a new plan would not be in the restore record.
        var target = PowerPlans.ListSchemes().Select(s => (Guid?)s.guid).FirstOrDefault(g => g == PowerPlans.UltimatePerformance || g == PowerPlans.HighPerformance);
        if (target is { } t && current != t)
        {
            RecordPower(rule, current, overlay);
            return PowerPlans.SetActive(t);
        }
        if (target is null && overlay is { } o && o != PowerPlans.OverlayBestPerformance)
        {
            RecordPower(rule, current, overlay);
            return PowerPlans.SetOverlay(PowerPlans.OverlayBestPerformance);
        }
        return false; // already as fast as it goes
    }

    private bool StockPower(TunerRule rule)
    {
        var (current, _) = PowerPlans.GetActive();
        var overlay = PowerPlans.GetOverlay();
        var changed = false;
        if (current is { } c && c != PowerPlans.Balanced)
        {
            RecordPower(rule, current, overlay);
            changed = PowerPlans.SetActive(PowerPlans.Balanced);
        }
        if (overlay == PowerPlans.OverlayBestPerformance)
        {
            if (!changed) RecordPower(rule, current, overlay);
            changed |= PowerPlans.SetOverlay(Guid.Empty); // Windows' default (Balanced) power mode
        }
        return changed;
    }

    private void RecordPower(TunerRule rule, Guid? scheme, Guid? overlay)
    {
        lock (_lock)
        {
            _state.Power.Add(new() { Rule = rule, PreviousScheme = scheme, PreviousOverlay = overlay });
            Persist();
        }
    }

    private bool SetFrameCap(TunerRule rule, int fps)
    {
        var before = hooks.GetCap() ?? 0;
        if (before == fps) return false;
        lock (_lock)
        {
            _state.Caps.Add(new() { Rule = rule, PreviousFps = before, RtssPath = hooks.RtssPath, RenderExe = hooks.RenderExe });
            Persist();
        }
        return hooks.SetCap(fps);
    }

    private bool Trim()
    {
        var keep = new HashSet<int>(hooks.GamePids()) { Environment.ProcessId };
        return MemoryCleaner.TrimBackgroundWorkingSets(keep) > 0;
    }

    private void Toast(string text)
    {
        var (cpu, gpu) = hooks.Temperatures();
        var hot = Math.Max(cpu ?? 0, gpu ?? 0);
        hooks.Toast?.Invoke(new TempAlert(gpu > cpu ? "GPU" : "CPU", hot, ClosedLoopTuner.HotC, text));
    }

    // ------------------------------------------------------------------ put back

    /// <summary>Puts back everything one rule changed. True if there was anything to put back.</summary>
    public bool Revert(TunerRule rule)
    {
        ClosedLoopRestoreState mine;
        lock (_lock)
        {
            mine = new ClosedLoopRestoreState
            {
                Priorities = [.. _state.Priorities.Where(e => e.Rule == rule)],
                Affinities = [.. _state.Affinities.Where(e => e.Rule == rule)],
                Power = [.. _state.Power.Where(e => e.Rule == rule)],
                Caps = [.. _state.Caps.Where(e => e.Rule == rule)],
                HeldBack = [.. _state.HeldBack.Where(e => e.Rule == rule)],
            };
            _state.Priorities.RemoveAll(e => e.Rule == rule);
            _state.Affinities.RemoveAll(e => e.Rule == rule);
            _state.Power.RemoveAll(e => e.Rule == rule);
            _state.Caps.RemoveAll(e => e.Rule == rule);
            _state.HeldBack.RemoveAll(e => e.Rule == rule);
        }
        var any = RestoreEntries(mine, apps => { foreach (var h in apps) resources.Undo(h.Name); }, hooks.SetCap, hooks.Log);
        lock (_lock) Persist();
        return any;
    }

    /// <summary>Everything is put back and the restore file is removed. Call once at the end of the session.</summary>
    public void Finish()
    {
        List<TunerRule> rules;
        lock (_lock)
        {
            rules = [.. _state.Priorities.Select(e => e.Rule).Concat(_state.Affinities.Select(e => e.Rule)).Concat(_state.Power.Select(e => e.Rule))
                .Concat(_state.Caps.Select(e => e.Rule)).Concat(_state.HeldBack.Select(e => e.Rule)).Distinct()];
        }
        foreach (var r in rules) Revert(r);
        try { File.Delete(paths.ClosedLoopState); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Call at launcher start: undoes changes left behind by a crash or power loss. Held-back background apps are put back by the resource guard's own recovery.</summary>
    public static List<string> RecoverIfNeeded(AppPaths paths)
    {
        if (!File.Exists(paths.ClosedLoopState)) return [];
        var report = new List<string>();
        ClosedLoopRestoreState? state;
        try { state = Json.Load<ClosedLoopRestoreState>(paths.ClosedLoopState); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return report; }
        if (state is not null)
        {
            state.HeldBack = [];
            if (RestoreEntries(state, _ => { }, null, null)) report.Add("Recovered what the telemetry tuner had changed");
            // The frame cap needs RTSS, which only start-up recovery knows the folder of.
            foreach (var cap in state.Caps.Where(c => c.RtssPath.Length > 0 && c.RenderExe.Length > 0))
            {
                try
                {
                    using var rtss = new RtssClient(cap.RtssPath);
                    rtss.ReloadProfile(cap.RenderExe);
                    rtss.SetFramerateLimit(cap.RenderExe, cap.PreviousFps);
                    report.Add("Frame cap put back");
                }
                catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
            }
        }
        try { File.Delete(paths.ClosedLoopState); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return report;
    }

    /// <summary>Puts entries back. Checks the process name first, so a reused process id is never touched.</summary>
    private static bool RestoreEntries(ClosedLoopRestoreState s, Action<List<ClosedLoopRestoreState.HeldBackEntry>> releaseApps, Func<int, bool>? setCap, Action<string>? log)
    {
        var any = false;
        foreach (var e in s.Priorities)
        {
            try
            {
                using var p = Process.GetProcessById(e.Pid);
                if (!string.Equals(p.ProcessName, e.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.PriorityClass == ProcessPriorityClass.High) { p.PriorityClass = e.Previous; any = true; }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        foreach (var e in s.Affinities)
        {
            try
            {
                using var p = Process.GetProcessById(e.Pid);
                if (!string.Equals(p.ProcessName, e.Name, StringComparison.OrdinalIgnoreCase)) continue;
                p.ProcessorAffinity = (nint)e.PreviousMask;
                any = true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        // Newest first, so two rules that both changed the power profile unwind in the right order.
        foreach (var e in Enumerable.Reverse(s.Power))
        {
            if (e.PreviousScheme is { } scheme && PowerPlans.SetActive(scheme)) any = true;
            if (e.PreviousOverlay is { } overlay && PowerPlans.SetOverlay(overlay)) any = true;
        }
        if (s.HeldBack.Count > 0) { releaseApps(s.HeldBack); any = true; }
        if (setCap is not null)
            foreach (var c in s.Caps)
            {
                if (setCap(c.PreviousFps)) any = true;
                else log?.Invoke("The frame cap could not be put back; RTSS may need a restart.");
            }
        return any;
    }

    private void Persist()
    {
        try { Json.Save(paths.ClosedLoopState, _state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { hooks.Log?.Invoke("Could not save the telemetry restore file: " + e.Message); }
    }
}
