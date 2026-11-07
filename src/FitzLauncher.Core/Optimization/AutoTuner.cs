using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

public enum AutoTuneActionKind { None, Apply, Revert, RevertThenApply, Done }

public sealed record AutoTuneDecision(AutoTuneActionKind Kind, string Message, Tweak? ToApply = null, AutoTuneStep? ToRevert = null);

/// <summary>
/// Hands-off tuning. After a session that misses the FPS target, it applies ONE graphics suggestion
/// (least visible first). The next session decides: keep it if it helped by a meaningful margin,
/// revert it otherwise. It repeats until the target is held, then stops. Pure logic: the caller
/// applies/reverts through <see cref="TweakEngine"/>.
/// </summary>
public static class AutoTuner
{
    public const double MinSessionMinutes = 5;
    /// <summary>1% low must improve by at least this much (or time-on-target by 3 points) to keep a change.</summary>
    public const double KeepThreshold = 0.03;

    public static bool MetTarget(SessionSummary s, int target) =>
        (s.TimeAtTarget ?? 0) >= 0.95 && s.OnePercentLowFps >= target * 0.93;

    public static AutoTuneDecision Evaluate(Game game, SessionSummary session, OptimizationPlan plan)
    {
        var state = game.AutoTune;
        if (!state.Enabled) return new(AutoTuneActionKind.None, "Auto-tune is off for this game.");
        if (game.FpsTarget is not { } target) return new(AutoTuneActionKind.None, "Auto-tune needs an FPS target.");
        if (!session.HasFpsData || session.Duration.TotalMinutes < MinSessionMinutes)
            return new(AutoTuneActionKind.None, $"Auto-tune waits for a session of at least {MinSessionMinutes:0} minutes with FPS data.");

        var pending = state.Steps.LastOrDefault(s => s.Outcome == AutoTuneOutcome.Pending);
        AutoTuneStep? revert = null;
        string verdict = "";
        double baseAvg = session.AverageFps, baseLow = session.OnePercentLowFps, baseAt = session.TimeAtTarget ?? 0;

        if (pending is not null)
        {
            if (pending.TargetFps != target)
            {
                // The target changed; the comparison would be meaningless.
                pending.Outcome = AutoTuneOutcome.Skipped;
            }
            else
            {
                pending.ResultAvg = session.AverageFps;
                pending.ResultLow = session.OnePercentLowFps;
                pending.ResultAtTarget = session.TimeAtTarget;
                var lowGain = pending.BaselineLow > 0 ? (session.OnePercentLowFps - pending.BaselineLow) / pending.BaselineLow : 0;
                var atGain = (session.TimeAtTarget ?? 0) - pending.BaselineAtTarget;
                if (lowGain >= KeepThreshold || atGain >= 0.03)
                {
                    pending.Outcome = AutoTuneOutcome.Kept;
                    verdict = $"Kept \"{pending.Title}\": 1% low {pending.BaselineLow:0} → {session.OnePercentLowFps:0}. ";
                }
                else
                {
                    pending.Outcome = AutoTuneOutcome.Reverted;
                    revert = pending;
                    verdict = $"Reverted \"{pending.Title}\": no real gain ({pending.BaselineLow:0} → {session.OnePercentLowFps:0}). ";
                    // Judge the next change against the session before this one, not the failed experiment.
                    baseAvg = pending.BaselineAvg; baseLow = pending.BaselineLow; baseAt = pending.BaselineAtTarget;
                }
            }
        }

        if (MetTarget(session, target) && revert is null)
        {
            state.TargetReached = true;
            return new(AutoTuneActionKind.Done, verdict + $"Target reached: {session.TimeAtTarget:P0} of frames at {target} FPS. Auto-tune is done.");
        }
        state.TargetReached = false;

        var tried = state.Steps.Select(s => s.TweakId).ToHashSet();
        var next = plan.Suggestions
            .Where(t => t.IsAutomatic && t.Risk != TweakRisk.Experimental && t.VisualImpact <= VisualImpact.Medium && t.OneClickEligible)
            .Where(t => t.Category == TweakCategory.Graphics || t.Category == TweakCategory.Stutter)
            .Where(t => !tried.Contains(t.Id))
            .OrderBy(t => t.VisualImpact)
            .ThenByDescending(t => t.Recommended)
            .FirstOrDefault();

        if (next is null)
        {
            var kind = revert is null ? AutoTuneActionKind.None : AutoTuneActionKind.Revert;
            return new(kind, verdict + "No more automatic changes to try. The remaining options are manual (see suggestions).", ToRevert: revert);
        }

        state.Steps.Add(new AutoTuneStep
        {
            TweakId = next.Id, Title = next.Title, TargetFps = target,
            BaselineAvg = baseAvg, BaselineLow = baseLow, BaselineAtTarget = baseAt,
        });
        return new(revert is null ? AutoTuneActionKind.Apply : AutoTuneActionKind.RevertThenApply,
            verdict + $"Trying \"{next.Title}\" next session ({next.ExpectedGain}).", next, revert);
    }
}
