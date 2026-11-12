using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Windows;

/// <summary>
/// Defender scan deferral and the scheduled-maintenance hold. Both are recorded in the Game Mode restore
/// state before anything changes, so <see cref="GameModeController.Restore"/> and its start-up recovery undo them
/// even after a crash. Real-time protection is never touched; if Windows refuses a change (Tamper Protection, a
/// work policy) nothing is recorded and the user is told in plain English. Decisions live in <see cref="SessionHoldPlanner"/>.
/// </summary>
internal sealed class SessionHolds(Action<string>? log)
{
    private const int TimeoutMs = 20_000;

    private HoldSnapshot? Read()
    {
        var (ok, output) = PowerShellRunner.Run(SessionHoldPlanner.ReadScript, TimeoutMs, log);
        return ok ? SessionHoldPlanner.ParseSnapshot(output) : null;
    }

    /// <summary>Applies the holds and returns report lines. <paramref name="persist"/> saves the restore state.</summary>
    public List<string> Apply(GameModeOptions o, GameModeRestoreState state, Action persist)
    {
        var report = new List<string>();
        if (!o.DeferDefenderScans && !o.HoldMaintenance) return report;
        var before = Read();
        if (before is null) { log?.Invoke("Session holds skipped: could not read Defender or task settings"); return report; }

        if (o.DeferDefenderScans)
        {
            var plan = SessionHoldPlanner.PlanDefender(before);
            if (plan.Count > 0)
            {
                state.DefenderChanges = plan;
                persist(); // record first, so a crash mid-change still restores
                PowerShellRunner.Run(SessionHoldPlanner.BuildDefenderScript(plan, apply: true), TimeoutMs, log);
                var after = Read();
                // If we can't re-read, keep the whole record: restore only touches values that still equal ours.
                var kept = after is null ? plan : SessionHoldPlanner.StillApplied(plan, after);
                state.DefenderChanges = kept;
                persist();
                if (kept.Count == 0) report.Add(SessionHoldPlanner.LeftAloneMessage);
                else report.Add("Defender: scheduled and catch-up scans set to idle-only and gentle for this session (real-time protection stays on)");
                if (kept.Count is > 0 && kept.Count < plan.Count) log?.Invoke($"Defender: {plan.Count - kept.Count} of {plan.Count} settings did not change (policy or Tamper Protection)");
            }
        }

        if (o.HoldMaintenance)
        {
            var plan = SessionHoldPlanner.PlanMaintenance(before);
            if (plan.DisableTask)
            {
                state.DefragTaskDisabled = true;
                persist();
            }
            PowerShellRunner.Run(SessionHoldPlanner.BuildMaintenanceScript(plan), TimeoutMs, log);
            if (plan.DisableTask)
            {
                if (Read()?.DefragState is "Disabled") report.Add("Drive optimise (TRIM/defrag) and Windows maintenance paused for this session");
                else { state.DefragTaskDisabled = false; persist(); log?.Invoke("Drive optimise task could not be disabled"); }
            }
        }
        return report;
    }

    /// <summary>Puts back only what is still ours. False when it could not be confirmed, so the caller keeps the record and retries.</summary>
    public bool Restore(GameModeRestoreState s, List<string> report)
    {
        if (s.DefenderChanges.Count == 0 && !s.DefragTaskDisabled) return true;
        var now = Read();
        if (now is null) return false;

        var ours = SessionHoldPlanner.StillApplied(s.DefenderChanges, now);
        if (ours.Count > 0)
        {
            PowerShellRunner.Run(SessionHoldPlanner.BuildDefenderScript(ours, apply: false), TimeoutMs, log);
            report.Add("Defender scan settings restored");
        }
        if (SessionHoldPlanner.ShouldReenableDefrag(s.DefragTaskDisabled, now))
        {
            PowerShellRunner.Run(SessionHoldPlanner.BuildReenableScript(), TimeoutMs, log);
            report.Add("Drive optimise and maintenance resumed");
        }

        if (ours.Count == 0 && !s.DefragTaskDisabled) return true;
        var check = Read();
        if (check is null) return false;
        return SessionHoldPlanner.StillApplied(ours, check).Count == 0
            && !SessionHoldPlanner.ShouldReenableDefrag(s.DefragTaskDisabled, check);
    }
}
