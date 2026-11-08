using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Fps;

public readonly record struct StutterEvent(DateTimeOffset At, float FrameMs);

/// <summary>What was going on at one moment of a session (sampled every 2 s).</summary>
public sealed record ContextSample(DateTimeOffset At, double? CpuTempC, double? GpuTempC, double? CpuLoad, double? GpuLoad,
    double? FreeRamPercent, string? TopBackgroundApp, double TopBackgroundCpu, bool ThermalThrottling);

public enum StutterCause { ShaderCompile, BackgroundApp, Memory, Thermal, CpuLimit, Unexplained }

public sealed record StutterCauseFinding(StutterCause Cause, int Count, string Title, string Detail, string Fix);
public sealed record StutterMoment(TimeSpan At, float WorstMs, int Frames, StutterCause Cause);

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
            if (cause == StutterCause.BackgroundApp && ctx.MaxBy(c => c.TopBackgroundCpu)?.TopBackgroundApp is { } app) apps[app] = apps.GetValueOrDefault(app) + m.Count;
            classified.Add(new StutterMoment(at - start, m.Max(x => x.FrameMs), m.Count, cause));
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
        if (ctx.Any(c => c.TopBackgroundCpu >= 12)) return StutterCause.BackgroundApp;
        if (ctx.Any(c => c.CpuLoad >= 92)) return StutterCause.CpuLimit;
        if (intoSession < TimeSpan.FromMinutes(4) || engineCompiles) return StutterCause.ShaderCompile;
        return StutterCause.Unexplained;
    }

    private static StutterCauseFinding Describe(StutterCause cause, int count, string? app) => cause switch
    {
        StutterCause.ShaderCompile => new(cause, count, "Shader compiling",
            "Short hitches while the game builds shaders for new areas and effects, common early in a session and in Unreal/DX12 games.",
            "They fade as the shader cache fills. Apply the stutter fixes on the game page, don't clear the shader cache after every driver update, and for DirectX 11 games try DXVK."),
        StutterCause.BackgroundApp => new(cause, count, $"A background app{(app is null ? "" : $" ({app})")}",
            "Another program used a lot of CPU at the same moment.",
            $"Turn on the background CPU limiter in Settings{(app is null ? "" : $", or close {app} before playing")}."),
        StutterCause.Memory => new(cause, count, "Memory nearly full",
            "Free RAM dropped below 8%, so Windows had to move data around.",
            "Close browsers and launchers before playing and keep Game Mode's memory guard on. On an APU, check the graphics memory (VRAM) setting isn't taking too much RAM."),
        StutterCause.Thermal => new(cause, count, "Overheating",
            "The CPU or GPU was at its temperature limit and slowing itself down.",
            "Use the Cool cooling mode for this game, lower the FPS cap a step, and keep vents clear."),
        StutterCause.CpuLimit => new(cause, count, "CPU maxed out",
            "The CPU was running flat out, so frames had to wait for it.",
            "Lower the FPS cap a step, try core pinning on the game page, and lower CPU-heavy settings (crowds, view distance, physics)."),
        _ => new(cause, count, "Uneven frame pacing",
            "Nothing else was busy at the time; the game itself delivered frames unevenly (streaming, traversal loading).",
            "Try the other RTSS limiter mode (front-edge sync), keep the game on the NVMe drive, and cap a little below your screen's refresh rate."),
    };
}
