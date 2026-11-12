using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>A change Smooth mode can make to improve the 1% lows. New ones go at the end: they are saved by name in the undo record.</summary>
public enum SmoothFix { LowerCap, AutoTdpOn, CoolMode, BackgroundApps, ResourceGuard }

/// <param name="Applied">Already on (made by Smooth mode), so the button undoes it.</param>
public sealed record SmoothSuggestion(SmoothFix Fix, string Title, string Why, bool Applied);

/// <summary>
/// Smooth mode: one switch per game that applies the safe, reversible settings which help the 1% lows in heavy scenes (fights, cities),
/// plus the same changes one at a time as suggestions from the last session. Every change writes what it replaced first, so it can be put back.
/// Only settings the launcher already owns are changed: nothing touches the game, the driver or Windows security features.
/// </summary>
public static class SmoothMode
{
    /// <summary>The 1% low is "smooth" at 90 % of the target, as in Auto TDP and the stutter detective.</summary>
    private const double SmoothShare = 0.90;
    private const double HotCelsius = 90;

    public static IReadOnlyList<SmoothSuggestion> Suggest(Game game, LauncherSettings settings, SessionSummary? last, bool autoTdpAvailable, HardwareProfile? hw = null)
    {
        var list = new List<SmoothSuggestion>();
        void Add(SmoothFix fix, bool needed, string title, string why)
        {
            var applied = game.SmoothUndo.ContainsKey(fix.ToString());
            if (needed || applied) list.Add(new SmoothSuggestion(fix, title, why, applied));
        }

        Add(SmoothFix.LowerCap, CapTarget(game, last, hw) is not null, "Lower the FPS cap a step",
            CapTarget(game, last, hw) is { } cap && last is not null
                ? $"Last session's 1% low was {last.OnePercentLowFps:0} FPS against a {game.FpsTarget ?? last.TargetFps} FPS target. A cap of {cap} FPS is one your PC held steadily, and a cap the game can hold gives even frame times."
                : "A cap the game can hold gives even frame times.");
        Add(SmoothFix.AutoTdpOn, autoTdpAvailable && (game.AutoTdp ?? settings.AutoTdp) != AutoTdpMode.On, "Auto TDP on for this game",
            "Gives the chip extra power in heavy scenes only when frames dip, and takes it back when they don't.");
        Add(SmoothFix.CoolMode, last is { MaxCpuTempC: >= HotCelsius } or { MaxGpuTempC: >= HotCelsius } && game.Cooling is null or CoolingMode.Off or CoolingMode.Balanced,
            "Use Cool mode for this game", "The chip reached 90 °C or more last session and slows itself down when hot, which shows up as dips.");
        Add(SmoothFix.BackgroundApps, !settings.BackgroundLimiter, "Slow background apps while I play",
            "A background app using the CPU for a moment is one of the commonest causes of a single bad frame.");
        Add(SmoothFix.ResourceGuard, !settings.ResourceGuard.Enabled, "Resource guard: free GPU, CPU and memory for the game",
            "Holds back busy background apps and trims memory when it runs short. Needs Game Mode on.");
        return list;
    }

    /// <summary>Things that help but that the launcher won't change for you, with the reason.</summary>
    public static IReadOnlyList<string> Advice(SessionSummary? last)
    {
        var advice = new List<string>
        {
            "Keep the game on your fastest drive (not a USB or SD card) and leave some free space: open-world games stream data in cities and fights.",
            "Don't clear the graphics driver's shader cache; hitches in new areas get fewer as it fills.",
            "In busy scenes lower crowd or NPC density, grass and view distance first: they cost the CPU, which is what usually holds the 1% lows down.",
        };
        if (last is { StutterCount: > 0 and var n } && last.Duration.TotalMinutes > 0 && n / last.Duration.TotalMinutes >= 1)
            advice.Add("Windows' Memory Integrity (virtualisation-based security) can cost 5-15 % of the 1% lows on some PCs, but turning it off weakens security, so Fitz never changes it. It's your call in Windows Security → Device security.");
        return advice;
    }

    /// <summary>The highest cap the 1% lows of the last session held, when that is lower than the current target; otherwise null.</summary>
    public static int? CapTarget(Game game, SessionSummary? last, HardwareProfile? hw = null)
    {
        if (last is not { HasFpsData: true }) return null;
        var current = game.FpsTarget ?? last.TargetFps;
        if (current is not > 0 || last.OnePercentLowFps >= current * SmoothShare) return null;
        var (target, _) = TargetAdvisor.Suggest(hw, last, current);
        return target is { } t && t < current ? t : null;
    }

    /// <summary>Turns Smooth mode on or off: on applies every suggestion that applies, off puts all of them back.</summary>
    public static string SetEnabled(Game game, LauncherSettings settings, bool on, SessionSummary? last, bool autoTdpAvailable, HardwareProfile? hw = null)
    {
        game.SmoothMode = on;
        var n = 0;
        if (on)
            foreach (var s in Suggest(game, settings, last, autoTdpAvailable, hw).Where(s => !s.Applied)) n += Apply(game, settings, s.Fix, last, hw) is null ? 0 : 1;
        else
            foreach (var key in game.SmoothUndo.Keys.ToList()) n += Undo(game, settings, Enum.Parse<SmoothFix>(key)) is null ? 0 : 1;
        return on ? (n > 0 ? $"Smooth mode is on: {n} change(s) made. Switch it off to put them all back." : "Smooth mode is on. Nothing needed changing.")
                  : (n > 0 ? $"Smooth mode is off: {n} change(s) put back." : "Smooth mode is off.");
    }

    /// <returns>A plain-English line saying what changed, or null when there was nothing to do.</returns>
    public static string? Apply(Game game, LauncherSettings settings, SmoothFix fix, SessionSummary? last, HardwareProfile? hw = null)
    {
        var key = fix.ToString();
        if (game.SmoothUndo.ContainsKey(key)) return null;
        switch (fix)
        {
            case SmoothFix.LowerCap when CapTarget(game, last, hw) is { } cap:
                game.SmoothUndo[key] = game.FpsTarget?.ToString() ?? "";
                game.FpsTarget = cap;
                return $"The FPS target is now {cap}.";
            case SmoothFix.AutoTdpOn when (game.AutoTdp ?? settings.AutoTdp) != AutoTdpMode.On:
                game.SmoothUndo[key] = game.AutoTdp?.ToString() ?? "";
                game.AutoTdp = AutoTdpMode.On;
                return "Auto TDP is on for this game.";
            case SmoothFix.CoolMode when game.Cooling is null or CoolingMode.Off or CoolingMode.Balanced:
                game.SmoothUndo[key] = game.Cooling?.ToString() ?? "";
                game.Cooling = CoolingMode.Cool;
                return "This game now uses the Cool cooling mode.";
            case SmoothFix.BackgroundApps when !settings.BackgroundLimiter:
                game.SmoothUndo[key] = "false";
                settings.BackgroundLimiter = true;
                return "Background apps that suddenly use a lot of CPU are now slowed down while you play.";
            case SmoothFix.ResourceGuard when !settings.ResourceGuard.Enabled:
                game.SmoothUndo[key] = "false";
                settings.ResourceGuard.Enabled = true;
                return "Resource guard is on (it needs Game Mode on in Settings).";
            default: return null;
        }
    }

    /// <returns>A line saying what was put back, or null when this change wasn't made by Smooth mode.</returns>
    public static string? Undo(Game game, LauncherSettings settings, SmoothFix fix)
    {
        var key = fix.ToString();
        if (!game.SmoothUndo.Remove(key, out var before)) return null;
        switch (fix)
        {
            case SmoothFix.LowerCap: game.FpsTarget = int.TryParse(before, out var t) ? t : null; return "The FPS target is back as it was.";
            case SmoothFix.AutoTdpOn: game.AutoTdp = Enum.TryParse<AutoTdpMode>(before, out var m) ? m : null; return "Auto TDP is back as it was for this game.";
            case SmoothFix.CoolMode: game.Cooling = Enum.TryParse<CoolingMode>(before, out var c) ? c : null; return "Cooling is back as it was for this game.";
            case SmoothFix.BackgroundApps: settings.BackgroundLimiter = false; return "Slowing background apps is off again.";
            case SmoothFix.ResourceGuard: settings.ResourceGuard.Enabled = false; return "Resource guard is off again.";
            default: return null;
        }
    }
}
