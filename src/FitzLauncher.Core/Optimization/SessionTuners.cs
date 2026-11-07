using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>
/// Per-game power limit for APUs. The target is to run at the lowest power that still holds the FPS
/// target: cooler, quieter, and more battery. Easy sessions step the limit down; missed targets step it up.
/// </summary>
public static class PowerTuner
{
    public const int StepDown = 3, StepUp = 5;

    public static int WattsFor(Game game, LauncherSettings s) =>
        Math.Clamp(game.PowerWatts ?? s.PowerDefaultWatts, s.PowerMinWatts, s.PowerMaxWatts);

    public static string? Learn(Game game, SessionSummary session, LauncherSettings s)
    {
        if (!game.PowerAuto || session.PowerWatts is not { } used || !session.HasFpsData || session.TargetFps is not { } target) return null;
        if (session.Duration.TotalMinutes < 3) return null;
        var easy = (session.TimeAtTarget ?? 0) >= 0.98 && session.OnePercentLowFps >= target * 0.97;
        var missed = (session.TimeAtTarget ?? 1) < 0.9 || session.OnePercentLowFps < target * 0.88;
        int? next = easy ? used - StepDown : missed ? used + StepUp : null;
        if (next is not { } n) return null;
        n = Math.Clamp(n, s.PowerMinWatts, s.PowerMaxWatts);
        if (n == used) return null;
        game.PowerWatts = n;
        return easy ? $"Power limit lowered to {n} W for next time ({target} FPS held easily)."
                    : $"Power limit raised to {n} W for next time (missed {target} FPS).";
    }
}

/// <summary>
/// Picks the smoother RTSS limiter mode per game by trying each once:
/// 0 = async (lowest latency), 1 = front-edge sync (most even pacing). Smoothness = 1% low ÷ average.
/// </summary>
public static class PacingTuner
{
    public static readonly int[] Modes = [0, 1];
    public static string Name(int mode) => mode == 1 ? "front-edge sync" : "async";

    /// <summary>The mode to use for the next session.</summary>
    public static int Next(Game game) =>
        game.LimiterChosen ?? Modes.FirstOrDefault(m => !game.LimiterResults.ContainsKey(m), Modes[0]);

    public static string? Learn(Game game, SessionSummary s)
    {
        if (s.LimiterMode is not { } mode || !s.HasFpsData || s.AverageFps <= 0 || s.Duration.TotalMinutes < 5) return null;
        var smooth = s.OnePercentLowFps / s.AverageFps;
        if (!game.LimiterResults.TryGetValue(mode, out var r)) game.LimiterResults[mode] = r = new LimiterResult();
        r.Smoothness = r.Sessions == 0 ? smooth : (r.Smoothness * r.Sessions + smooth) / (r.Sessions + 1);
        r.Sessions++;
        if (game.LimiterChosen is null && Modes.All(game.LimiterResults.ContainsKey))
        {
            var best = Modes.OrderByDescending(m => game.LimiterResults[m].Smoothness).First();
            game.LimiterChosen = best;
            return $"RTSS limiter: {Name(best)} was smoother for this game ({game.LimiterResults[best].Smoothness:P0} vs {game.LimiterResults[1 - best].Smoothness:P0} 1%-low/avg), so it's kept.";
        }
        return game.LimiterChosen is null ? $"RTSS limiter: tested {Name(mode)}; {Name(1 - mode)} gets tried next session." : null;
    }
}
