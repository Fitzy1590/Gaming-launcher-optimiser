using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Fps;

public readonly record struct StutterEvent(DateTimeOffset At, float FrameMs);

/// <summary>What was going on at one moment of a session (sampled every 2 s).</summary>
public sealed record ContextSample(DateTimeOffset At, double? CpuTempC, double? GpuTempC, double? CpuLoad, double? GpuLoad,
    double? FreeRamPercent, string? TopBackgroundApp, double TopBackgroundCpu, bool ThermalThrottling,
    double? DiskBusyPercent = null, bool PowerLimitChanged = false);

// New causes are added at the end: reports are saved as numbers.
public enum StutterCause { ShaderCompile, BackgroundApp, Memory, Thermal, CpuLimit, Unexplained, DiskBusy, PowerChange }

/// <summary>A change the launcher can make for the game by itself (always reversible from the game page).</summary>
public enum StutterFix { None, LimitBackgroundApps, CoolMode, AutoTdpWatchOnly }

public sealed record StutterCauseFinding(StutterCause Cause, int Count, string Title, string Detail, string Fix,
    StutterFix Action = StutterFix.None, string ActionLabel = "");
public sealed record StutterMoment(TimeSpan At, float WorstMs, int Frames, StutterCause Cause, string Evidence = "");

/// <summary>Applies a suggested stutter fix to a game's own settings.</summary>
public static class StutterFixes
{
    /// <returns>A plain-English line saying what changed, or null when there was nothing to do.</returns>
    public static string? Apply(Game game, LauncherSettings settings, StutterFix fix)
    {
        switch (fix)
        {
            case StutterFix.LimitBackgroundApps when !settings.BackgroundLimiter:
                settings.BackgroundLimiter = true;
                return "Background apps that suddenly use a lot of CPU are now slowed down while you play.";
            case StutterFix.CoolMode when game.Cooling is null or Optimization.CoolingMode.Off or Optimization.CoolingMode.Balanced:
                game.Cooling = Optimization.CoolingMode.Cool;
                return "This game now uses the Cool cooling mode.";
            case StutterFix.AutoTdpWatchOnly when game.AutoTdp != Optimization.AutoTdpMode.Observe:
                game.AutoTdp = Optimization.AutoTdpMode.Observe;
                return "Auto TDP now only watches for this game: it no longer changes the power limit.";
            default: return null;
        }
    }
}

public sealed class StutterReport
{
    public Guid GameId { get; set; }
    public DateTimeOffset SessionStart { get; set; }
    public int TotalStutters { get; set; }
    public double PerMinute { get; set; }
    public string Summary { get; set; } = "";
    public List<StutterCauseFinding> Causes { get; set; } = [];
    public List<StutterMoment> WorstMoments { get; set; } = [];
}

/// <summary>
/// Explains a session's stutters: groups spikes into moments and matches each with what the PC was doing
/// (shader compiling early on, a background app's CPU spike, memory nearly full, thermal throttling, the CPU
/// maxed out), then suggests the fix that addresses the biggest cause.
/// </summary>
public static class StutterDetective
{
    public static StutterReport Analyze(Game game, DateTimeOffset start, DateTimeOffset end, IReadOnlyList<StutterEvent> stutters, IReadOnlyList<ContextSample> samples, double cpuAlertC, double gpuAlertC)
    {
        var minutes = Math.Max((end - start).TotalMinutes, 0.1);
        var report = new StutterReport { GameId = game.Id, SessionStart = start, TotalStutters = stutters.Count, PerMinute = stutters.Count / minutes };
        if (stutters.Count == 0) { report.Summary = "No stutters recorded: frame pacing was smooth."; return report; }

        // Group spikes less than 3 s apart into moments.
        var moments = new List<List<StutterEvent>>();
        foreach (var e in stutters.OrderBy(x => x.At))
        {
            if (moments.Count > 0 && (e.At - moments[^1][^1].At).TotalSeconds < 3) moments[^1].Add(e);
            else moments.Add([e]);
        }

        var engineCompiles = game.LastScan?.Engine is GameEngine.Unreal4 or GameEngine.Unreal5 or GameEngine.UnrealUnknownVersion or GameEngine.Unity || game.LastScan?.UsesDx12 == true;
        var counts = new Dictionary<StutterCause, int>();
        var apps = new Dictionary<string, int>();
        var classified = new List<StutterMoment>();
        foreach (var m in moments)
        {
            var at = m[0].At;
            var ctx = samples.Where(c => Math.Abs((c.At - at).TotalSeconds) <= 3).ToList();
            var cause = Classify(at - start, ctx, engineCompiles, cpuAlertC, gpuAlertC);
            counts[cause] = counts.GetValueOrDefault(cause) + m.Count;
            var evidence = Evidence(ctx, cause);
            if (cause == StutterCause.BackgroundApp && ctx.MaxBy(c => c.TopBackgroundCpu)?.TopBackgroundApp is { } app) apps[app] = apps.GetValueOrDefault(app) + m.Count;
            classified.Add(new StutterMoment(at - start, m.Max(x => x.FrameMs), m.Count, cause, evidence));
        }

        foreach (var (cause, count) in counts.OrderByDescending(kv => kv.Value))
            report.Causes.Add(Describe(cause, count, apps.OrderByDescending(a => a.Value).Select(a => a.Key).FirstOrDefault()));
        report.WorstMoments = classified.OrderByDescending(x => x.WorstMs).Take(8).OrderBy(x => x.At).ToList();

        var top = report.Causes[0];
        report.Summary = report.PerMinute < 0.5
            ? $"Only {stutters.Count} stutter(s) in {minutes:0} minutes: smooth overall. Most were: {top.Title.ToLowerInvariant()}."
            : $"{stutters.Count} stutters ({report.PerMinute:0.#} a minute). Main cause: {top.Title.ToLowerInvariant()}. {top.Fix}";
        return report;
    }

    private static StutterCause Classify(TimeSpan intoSession, List<ContextSample> ctx, bool engineCompiles, double cpuAlertC, double gpuAlertC)
    {
        if (ctx.Any(c => c.ThermalThrottling || c.CpuTempC >= cpuAlertC - 2 || c.GpuTempC >= gpuAlertC - 2)) return StutterCause.Thermal;
        if (ctx.Any(c => c.FreeRamPercent is < 8)) return StutterCause.Memory;
        if (ctx.Any(c => c.DiskBusyPercent >= 90)) return StutterCause.DiskBusy;
        if (ctx.Any(c => c.PowerLimitChanged)) return StutterCause.PowerChange;
        if (ctx.Any(c => c.TopBackgroundCpu >= 12)) return StutterCause.BackgroundApp;
        if (ctx.Any(c => c.CpuLoad >= 92)) return StutterCause.CpuLimit;
        if (intoSession < TimeSpan.FromMinutes(4) || engineCompiles) return StutterCause.ShaderCompile;
        return StutterCause.Unexplained;
    }

    /// <summary>One line of proof for a stutter moment: what the PC was doing at that time.</summary>
    private static string Evidence(List<ContextSample> ctx, StutterCause cause)
    {
        if (ctx.Count == 0) return "";
        switch (cause)
        {
            case StutterCause.BackgroundApp when ctx.MaxBy(c => c.TopBackgroundCpu) is { TopBackgroundApp: { } app } c:
                return $"{app} was using {c.TopBackgroundCpu:0}% of the CPU";
            case StutterCause.DiskBusy:
                return $"the drive was {ctx.Max(c => c.DiskBusyPercent):0}% busy";
            case StutterCause.PowerChange:
                return "the power limit was changed at that moment";
            case StutterCause.Memory:
                return $"free memory was down to {ctx.Min(c => c.FreeRamPercent):0}%";
            case StutterCause.Thermal:
                return $"temperature was {Math.Max(ctx.Max(c => c.CpuTempC) ?? 0, ctx.Max(c => c.GpuTempC) ?? 0):0}°C and the chip was slowing itself down";
            case StutterCause.CpuLimit:
                return $"the CPU was at {ctx.Max(c => c.CpuLoad):0}% load";
            default: return "";
        }
    }

    private static StutterCauseFinding Describe(StutterCause cause, int count, string? app) => cause switch
    {
        StutterCause.ShaderCompile => new(cause, count, "Shader compiling",
            "Short hitches while the game builds shaders for new areas and effects, common early in a session and in Unreal/DX12 games.",
            "They fade as the shader cache fills. Apply the stutter fixes on the game page, don't clear the shader cache after every driver update, and for DirectX 11 games try DXVK."),
        StutterCause.BackgroundApp => new(cause, count, $"A background app{(app is null ? "" : $" ({app})")}",
            "Another program used a lot of CPU at the same moment.",
            $"Slow background apps down while you play{(app is null ? "" : $", or close {app} before playing")}.",
            StutterFix.LimitBackgroundApps, "Slow background apps while I play"),
        StutterCause.DiskBusy => new(cause, count, "The drive was busy",
            "The drive was close to 100% busy at the moment of the stutter (a download, update, virus scan or the game streaming data), so the game waited for data.",
            "Pause downloads, updates and scans while you play, keep the game on your fastest drive (not a USB or SD card), and leave some free space on it."),
        StutterCause.PowerChange => new(cause, count, "The power limit changed",
            "The launcher (or another tool such as OneXConsole) changed the power limit at the same moment as a stutter, so the chip had to change speed.",
            "Set Auto TDP to Watch only for this game, or give it a narrower range so it changes power less often. Turn off OneXConsole's own TDP control for the game.",
            StutterFix.AutoTdpWatchOnly, "Auto TDP: watch only for this game"),
        StutterCause.Memory => new(cause, count, "Memory nearly full",
            "Free RAM dropped below 8%, so Windows had to move data around.",
            "Close browsers and launchers before playing and keep Game Mode's memory guard on. On an APU, check the graphics memory (VRAM) setting isn't taking too much RAM."),
        StutterCause.Thermal => new(cause, count, "Overheating",
            "The CPU or GPU was at its temperature limit and slowing itself down.",
            "Use the Cool cooling mode for this game, lower the FPS cap a step, and keep vents clear.",
            StutterFix.CoolMode, "Use Cool mode for this game"),
        StutterCause.CpuLimit => new(cause, count, "CPU maxed out",
            "The CPU was running flat out, so frames had to wait for it.",
            "Lower the FPS cap a step, try core pinning on the game page, and lower CPU-heavy settings (crowds, view distance, physics)."),
        _ => new(cause, count, "Uneven frame pacing",
            "Nothing else was busy at the time; the game itself delivered frames unevenly (streaming, traversal loading).",
            "Try the other RTSS limiter mode (front-edge sync), keep the game on the NVMe drive, and cap a little below your screen's refresh rate."),
    };
}
