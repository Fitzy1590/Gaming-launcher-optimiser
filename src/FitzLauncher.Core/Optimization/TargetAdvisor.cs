using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>Picks a sensible FPS target from the display and from what past sessions actually achieved.</summary>
public static class TargetAdvisor
{
    public static (int? target, string reason) Suggest(HardwareProfile? hw, SessionSummary? last, int? currentTarget)
    {
        var refresh = hw?.PrimaryDisplay?.MaxRefreshHz ?? 0;
        var allowed = LauncherSettings.FpsTargets.Where(t => refresh == 0 || t <= refresh - 2).ToArray();
        if (refresh is > 0 and < 92)
            return (null, $"Your display runs at {refresh} Hz, below the 90 FPS target, so higher caps would only cause tearing. Leave this game uncapped, or cap it at {refresh - 3} FPS with RTSS.");

        if (last is { HasFpsData: true })
        {
            // Highest target that the 1% lows can hold (with a 5% allowance): stable rather than peaky.
            var holdable = allowed.Where(t => last.OnePercentLowFps >= t * 0.95).DefaultIfEmpty(0).Max();
            if (holdable > 0)
                return (holdable, $"Last session: {last.AverageFps:0} FPS average, 1% low {last.OnePercentLowFps:0}. {holdable} FPS is the highest target your PC held steadily.");
            if (allowed.Length > 0)
                return (allowed.Min(), $"Last session's 1% low was {last.OnePercentLowFps:0} FPS. Use the {allowed.Min()} FPS target and apply the graphics suggestions below to reach it.");
        }

        if (refresh >= 120)
            return (117, $"Your {refresh} Hz display suits 117 FPS: just under 120, which keeps G-Sync/FreeSync engaged with no tearing or V-Sync lag.");
        if (allowed.Length > 0)
            return (allowed.Max(), $"Highest target under your {refresh} Hz refresh rate.");
        return (currentTarget, "Play one session so the launcher can measure your FPS.");
    }
}
