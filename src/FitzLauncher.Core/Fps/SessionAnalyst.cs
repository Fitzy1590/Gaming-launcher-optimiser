using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Fps;

public enum AnalysisKind { FrameDrop, TempSpike }

// New causes are added at the end: reports are saved as numbers.
public enum AnalysisCause { Overheating, MemoryFull, DriveBusy, BackgroundApp, PowerLimitChange, GpuBound, CpuBound, GameSide, HeavyScene, PowerRaised, CoolingProblem, Uncapped }

/// <summary>Where a suggestion is carried out: in the game's own menus, or outside it (launcher, Windows, hardware).</summary>
public enum SuggestionScope { InGame, OutOfGame }

public sealed record AnalysisSuggestion(SuggestionScope Scope, string Text, StutterFix Fix = StutterFix.None, string FixLabel = "");

/// <summary>One cause of frame drops or temperature spikes found in a session, with what to do about it.</summary>
public sealed record AnalysisFinding(AnalysisKind Kind, AnalysisCause Cause, int Episodes, int Seconds, string Title, string Detail,
    string Evidence, List<AnalysisSuggestion> Suggestions)
{
    /// <summary>Short line for the in-game overlay.</summary>
    public string Short => Kind == AnalysisKind.FrameDrop ? $"Frame drops: {Title.ToLowerInvariant()}" : $"Temperature spike: {Title.ToLowerInvariant()}";
}

public sealed class SessionAnalysis
{
    public Guid GameId { get; set; }
    public DateTimeOffset SessionStart { get; set; }
    public string Summary { get; set; } = "";
    public List<AnalysisFinding> Findings { get; set; } = [];
}

/// <summary>The last few analyses of one game, so a cause that keeps coming back can be called out.</summary>
public sealed class GameAnalysisHistory
{
    public const int Keep = 5;
    public Guid GameId { get; set; }
    public List<SessionAnalysis> Sessions { get; set; } = [];

    public void Add(SessionAnalysis a)
    {
        Sessions.RemoveAll(s => s.SessionStart == a.SessionStart);
        Sessions.Add(a);
        Sessions = [.. Sessions.OrderByDescending(s => s.SessionStart).Take(Keep)];
    }

    /// <summary>How many of the saved sessions (including the newest) showed this cause.</summary>
    public int SessionsWith(AnalysisKind kind, AnalysisCause cause) =>
        Sessions.Count(s => s.Findings.Any(f => f.Kind == kind && f.Cause == cause));
}

/// <summary>
/// Looks through a session's 2-second samples for sustained frame drops and fast temperature rises, works out why each
/// happened from what else was going on, and suggests changes for this game: some in the game's own settings, some outside it.
/// Pure: the caller supplies samples and applies any fix. Safe to run repeatedly on a session that is still going.
/// </summary>
public static class SessionAnalyst
{
    public const double DropShare = 0.85;         // below 85 % of the usual FPS counts as a drop
    public const int MinDropSamples = 3;          // 3 samples in a row = 6 s
    public const double SpikeRiseC = 8;           // a rise of 8 °C ...
    public const int SpikeWindowSamples = 5;      // ... within 5 samples (10 s)
    public const double WarmUpSeconds = 30;       // loading and shader compiling at the start are left out
    public const double MaxGapSeconds = 6;        // a longer gap in the samples breaks an episode

    public static SessionAnalysis Analyze(Game game, DateTimeOffset start, IReadOnlyList<ContextSample> samples, double cpuAlertC, double gpuAlertC)
    {
        var result = new SessionAnalysis { GameId = game.Id, SessionStart = start };
        var play = samples.Where(s => (s.At - start).TotalSeconds >= WarmUpSeconds).OrderBy(s => s.At).ToList();
        var findings = new List<AnalysisFinding>();
        findings.AddRange(FrameDrops(game, play, cpuAlertC, gpuAlertC));
        findings.AddRange(TempSpikes(game, play, cpuAlertC, gpuAlertC));
        result.Findings = [.. findings.OrderByDescending(f => f.Seconds)];
        result.Summary = result.Findings.Count == 0
            ? (play.Count < 10 ? "Not enough play time to analyse yet." : "No frame drops or temperature spikes worth acting on.")
            : $"{result.Findings.Count(f => f.Kind == AnalysisKind.FrameDrop)} cause(s) of frame drops, {result.Findings.Count(f => f.Kind == AnalysisKind.TempSpike)} cause(s) of temperature spikes. Biggest: {result.Findings[0].Title.ToLowerInvariant()}.";
        return result;
    }

    // ---------------------------------------------------------------- frame drops

    private static IEnumerable<AnalysisFinding> FrameDrops(Game game, List<ContextSample> play, double cpuAlertC, double gpuAlertC)
    {
        var withFps = play.Where(s => s.Fps is > 0).ToList();
        if (withFps.Count < 10) return [];
        var typical = withFps.Select(s => s.Fps!.Value).OrderBy(x => x).ElementAt(withFps.Count / 2);
        var baseline = game.FpsTarget is > 0 ? Math.Min(game.FpsTarget.Value, typical * 1.15) : typical; // a target the PC never reaches is not the baseline
        var floor = baseline * DropShare;

        var counts = new Dictionary<AnalysisCause, (int Episodes, int Seconds, string Evidence)>();
        foreach (var run in Runs(withFps, s => s.Fps!.Value < floor, MinDropSamples))
        {
            var cause = ClassifyDrop(run, cpuAlertC, gpuAlertC, out var evidence);
            var secs = Seconds(run);
            var (e, s0, ev) = counts.TryGetValue(cause, out var prev) ? prev : (0, 0, "");
            counts[cause] = (e + 1, s0 + secs, ev.Length > 0 ? ev : evidence);
        }
        return counts.Select(kv => Describe(game, AnalysisKind.FrameDrop, kv.Key, kv.Value.Episodes, kv.Value.Seconds, kv.Value.Evidence, baseline));
    }

    private static AnalysisCause ClassifyDrop(List<ContextSample> run, double cpuAlertC, double gpuAlertC, out string evidence)
    {
        double? Max(Func<ContextSample, double?> f) => run.Select(f).Where(v => v is not null).Max();
        double? Avg(Func<ContextSample, double?> f) { var v = run.Select(f).Where(x => x is not null).Select(x => x!.Value).ToList(); return v.Count > 0 ? v.Average() : null; }

        if (run.Any(c => c.ThermalThrottling || c.CpuTempC >= cpuAlertC - 2 || c.GpuTempC >= gpuAlertC - 2))
        {
            evidence = $"it reached {Math.Max(Max(c => c.CpuTempC) ?? 0, Max(c => c.GpuTempC) ?? 0):0} °C and the chip was slowing itself down";
            return AnalysisCause.Overheating;
        }
        if (run.Any(c => c.FreeRamPercent is < 8)) { evidence = $"free memory fell to {run.Min(c => c.FreeRamPercent):0}%"; return AnalysisCause.MemoryFull; }
        if (run.Any(c => c.DiskBusyPercent >= 90)) { evidence = $"the drive was {Max(c => c.DiskBusyPercent):0}% busy"; return AnalysisCause.DriveBusy; }
        if (run.Any(c => c.TopBackgroundCpu >= 12))
        {
            var app = run.MaxBy(c => c.TopBackgroundCpu)!;
            evidence = $"{app.TopBackgroundApp ?? "another app"} was using {app.TopBackgroundCpu:0}% of the CPU";
            return AnalysisCause.BackgroundApp;
        }
        if (run.Any(c => c.PowerLimitChanged)) { evidence = "the power limit changed at the start of the drop"; return AnalysisCause.PowerLimitChange; }
        var gpu = Avg(c => c.GpuLoad);
        var cpu = Avg(c => c.CpuLoad);
        if (gpu >= 95 && (cpu ?? 0) < 90) { evidence = $"the graphics chip was at {gpu:0}% while the CPU was at {cpu?.ToString("0") ?? "?"}%"; return AnalysisCause.GpuBound; }
        if (cpu >= 90 || Max(c => c.CpuLoad) >= 97) { evidence = $"the CPU was at {cpu:0}% load"; return AnalysisCause.CpuBound; }
        evidence = "nothing else was busy at the time";
        return AnalysisCause.GameSide;
    }

    // ---------------------------------------------------------------- temperature spikes

    private static IEnumerable<AnalysisFinding> TempSpikes(Game game, List<ContextSample> play, double cpuAlertC, double gpuAlertC)
    {
        var counts = new Dictionary<AnalysisCause, (int Episodes, int Seconds, string Evidence, string Part)>();
        foreach (var (part, temp, alert) in new (string, Func<ContextSample, double?>, double)[] { ("CPU", c => c.CpuTempC, cpuAlertC), ("GPU", c => c.GpuTempC, gpuAlertC) })
        {
            var s = play.Where(c => temp(c) is not null).ToList();
            var lastEnd = -1;
            for (var i = 1; i < s.Count; i++)
            {
                if (i <= lastEnd) continue;
                var from = Math.Max(0, i - SpikeWindowSamples + 1);
                var low = Enumerable.Range(from, i - from).MinBy(j => temp(s[j])!.Value);
                if (temp(s[i])!.Value - temp(s[low])!.Value < SpikeRiseC || (s[i].At - s[low].At).TotalSeconds > MaxGapSeconds * SpikeWindowSamples) continue;
                // Follow the spike to its top.
                var peak = i;
                while (peak + 1 < s.Count && temp(s[peak + 1])!.Value >= temp(s[peak])!.Value && (s[peak + 1].At - s[peak].At).TotalSeconds <= MaxGapSeconds) peak++;
                lastEnd = peak;
                var span = s.GetRange(low, peak - low + 1);
                var cause = ClassifyTemp(game, span, temp(s[peak])!.Value, alert, out var evidence);
                var secs = Seconds(span);
                var (e, s0, ev, p) = counts.TryGetValue(cause, out var prev) ? prev : (0, 0, "", "");
                counts[cause] = (e + 1, s0 + secs, ev.Length > 0 ? ev : $"{part} went from {temp(s[low]):0} to {temp(s[peak]):0} °C in {Math.Max(secs, 2)} s; {evidence}", p.Length > 0 && p != part ? "CPU and GPU" : part);
            }
        }
        return counts.Select(kv => Describe(game, AnalysisKind.TempSpike, kv.Key, kv.Value.Episodes, kv.Value.Seconds, kv.Value.Evidence, 0, kv.Value.Part));
    }

    private static AnalysisCause ClassifyTemp(Game game, List<ContextSample> span, double peakC, double alertC, out string evidence)
    {
        var first = span[0];
        var last = span[^1];
        var loadRise = (last.CpuLoad ?? last.GpuLoad ?? 0) - (first.CpuLoad ?? first.GpuLoad ?? 0);
        var peakLoad = Math.Max(last.CpuLoad ?? 0, last.GpuLoad ?? 0);
        var wattRise = (last.Watts ?? 0) - (first.Watts ?? 0);

        if (peakLoad < 60 && peakC >= alertC - 10) { evidence = $"load was only {peakLoad:0}%, so the heat isn't coming from the game"; return AnalysisCause.CoolingProblem; }
        if (span.Any(c => c.PowerLimitChanged) || wattRise >= 5) { evidence = wattRise >= 5 ? $"power draw rose by {wattRise:0} W" : "the power limit went up"; return AnalysisCause.PowerRaised; }
        if (loadRise >= 25) { evidence = $"load jumped by {loadRise:0} points (a heavy scene, loading or a cutscene)"; return AnalysisCause.HeavyScene; }
        if (game.FpsTarget is not > 0 && span.Any(c => c.Fps > 100)) { evidence = "the frame rate had no cap"; return AnalysisCause.Uncapped; }
        evidence = $"load stayed high ({peakLoad:0}%)";
        return AnalysisCause.HeavyScene;
    }

    // ---------------------------------------------------------------- suggestions

    private static AnalysisFinding Describe(Game game, AnalysisKind kind, AnalysisCause cause, int episodes, int seconds, string evidence, double baseline, string part = "")
    {
        var scan = game.LastScan;
        var upscaler = scan is { Upscalers.Count: > 0 } ? string.Join("/", scan.Upscalers.Select(u => u switch { Upscaler.Dlss or Upscaler.DlssFrameGen => "DLSS", Upscaler.Fsr => "FSR", _ => "XeSS" }).Distinct()) : null;
        var inGame = (string text) => new AnalysisSuggestion(SuggestionScope.InGame, text);
        var outGame = (string text, StutterFix fix = StutterFix.None, string label = "") => new AnalysisSuggestion(SuggestionScope.OutOfGame, text, fix, label);
        var upscale = upscaler is not null ? $"Turn on {upscaler} (Quality or Balanced) in the graphics menu." : "Lower the resolution scale or render resolution a step.";
        var heavyOptions = scan?.Engine is GameEngine.Unreal4 or GameEngine.Unreal5 or GameEngine.UnrealUnknownVersion
            ? "Drop Shadows, Global Illumination, Effects and Post Processing from Epic/High to Medium."
            : "Drop Shadows, Volumetric/Fog and Reflections a step.";

        string title, detail;
        List<AnalysisSuggestion> sug;
        switch (cause)
        {
            case AnalysisCause.Overheating:
                title = "Overheating"; detail = "The chip hit its temperature limit and slowed itself down, which is what dropped the frame rate.";
                sug = [inGame("Cap the frame rate in the game's menu a little lower, and turn off VSync-less uncapped modes."), inGame(upscale),
                       outGame("Use the Cool cooling mode for this game.", StutterFix.CoolMode, "Use Cool mode for this game"),
                       outGame("Lower this game's FPS target a step.", StutterFix.LowerFpsTarget, "Lower the FPS target one step"),
                       outGame("Keep vents clear and the PC off soft surfaces; dust is the usual reason it throttles after a few months.")];
                break;
            case AnalysisCause.MemoryFull:
                title = "Memory nearly full"; detail = "Free RAM fell below 8%, so Windows had to shuffle data while the game ran.";
                sug = [inGame("Lower Texture Quality and Texture Streaming Pool a step; textures are the biggest memory user."), inGame("Close any in-game browser, mod menus or high-resolution texture packs."),
                       outGame("Close browsers and other launchers before playing; keep Game Mode's memory guard on."),
                       outGame("On a handheld, check the graphics memory (VRAM) setting in BIOS isn't taking too much of your RAM.")];
                break;
            case AnalysisCause.DriveBusy:
                title = "The drive was busy"; detail = "The drive was close to 100% busy while the frame rate fell, so the game waited for data.";
                sug = [inGame("Turn off texture streaming 'on demand' options, or set Texture Quality lower so less is loaded from disk."),
                       outGame("Pause downloads, updates and virus scans while you play, and keep the game on your fastest drive (not USB or an SD card)."),
                       outGame("Leave at least 15% of the drive free.")];
                break;
            case AnalysisCause.BackgroundApp:
                title = "A background app"; detail = "Another program used a lot of the CPU at the same time as the drops.";
                sug = [outGame("Slow background apps down while you play.", StutterFix.LimitBackgroundApps, "Slow background apps while I play"),
                       outGame("Close the app named below before playing, or stop it from starting with Windows.")];
                break;
            case AnalysisCause.PowerLimitChange:
                title = "The power limit changed"; detail = "The power limit was changed (by Auto TDP or another tool) right as the frame rate dropped.";
                sug = [outGame("Set Auto TDP to Watch only for this game so it stops changing the limit mid-game.", StutterFix.AutoTdpWatchOnly, "Auto TDP: watch only for this game"),
                       outGame("Turn off OneXConsole's or the vendor app's own TDP control for this game.")];
                break;
            case AnalysisCause.GpuBound:
                title = "Graphics chip maxed out"; detail = $"The graphics chip was at its limit while the CPU had room, so graphics settings decide the frame rate here{(baseline > 0 ? $" (usually about {baseline:0} FPS)" : "")}.";
                sug = [inGame(upscale), inGame(heavyOptions), inGame("Turn off or lower Ray Tracing, and lower Anti-Aliasing (TAA/MSAA) a step."),
                       outGame("Lower this game's FPS target a step so the chip isn't pushed past what it can hold.", StutterFix.LowerFpsTarget, "Lower the FPS target one step"),
                       outGame("On an APU, raise the power limit if it's low (Auto TDP does this automatically).")];
                break;
            case AnalysisCause.CpuBound:
                title = "CPU maxed out"; detail = "The CPU was running flat out, so frames waited for it. Lowering graphics won't help much here.";
                sug = [inGame("Lower CPU-heavy settings: crowd/NPC density, view distance, physics and simulation quality."), inGame("Turn off frame-rate-hungry extras such as high shadow draw distance."),
                       outGame("Try core pinning for this game on its page, and keep the CPU from sharing with background apps.", StutterFix.LimitBackgroundApps, "Slow background apps while I play"),
                       outGame("Lower the FPS target a step to give the CPU headroom.", StutterFix.LowerFpsTarget, "Lower the FPS target one step")];
                break;
            case AnalysisCause.HeavyScene:
                title = "A heavy scene"; detail = "The temperature rose quickly as the game's load jumped (a busy area, loading or a cutscene).";
                sug = [inGame("Cap the frame rate in the game's menu, especially in menus, maps and cutscenes."), inGame(upscale),
                       outGame("Turn on the thermal guard so the launcher eases the cap off before it throttles.", StutterFix.ThermalGuardOn, "Turn on the thermal guard"),
                       outGame("Use the Cool cooling mode for this game.", StutterFix.CoolMode, "Use Cool mode for this game")];
                break;
            case AnalysisCause.PowerRaised:
                title = "Power limit went up"; detail = "The temperature rose right after the power limit (or the power draw) went up.";
                sug = [outGame("Lower the maximum for Auto TDP, or give this game a fixed lower power limit.", StutterFix.AutoTdpWatchOnly, "Auto TDP: watch only for this game"),
                       outGame("Use the Cool cooling mode, which trims power while hot.", StutterFix.CoolMode, "Use Cool mode for this game"),
                       inGame("Cap the frame rate so the chip doesn't ask for full power for frames you can't see.")];
                break;
            case AnalysisCause.CoolingProblem:
                title = "Cooling, not the game"; detail = "It got hot while barely working, which points to the PC's cooling rather than this game.";
                sug = [outGame("Clean the vents and fans, check nothing blocks the intake, and keep the PC off soft surfaces."),
                       outGame("Use a faster fan curve (OneXConsole or the vendor app) and try the Cool cooling mode.", StutterFix.CoolMode, "Use Cool mode for this game"),
                       outGame("A warm room matters: a few degrees of room temperature is a few degrees of chip temperature.")];
                break;
            case AnalysisCause.Uncapped:
                title = "No frame cap"; detail = "The game ran with no frame cap, so the chip worked at full speed on frames that add little.";
                sug = [inGame("Turn on VSync or set the game's own frame limit to your screen's refresh rate."),
                       outGame("Set an FPS target for this game.", StutterFix.LowerFpsTarget, "Set a 60 FPS target")];
                break;
            default:
                title = "The game itself"; detail = "Nothing else was busy at the time, so the game delivered frames unevenly (streaming, loading new areas, or a poorly optimised area).";
                sug = [inGame("Update the game and graphics driver, and turn off any in-game 'unlimited' frame rate or exclusive-fullscreen options that cause problems."),
                       inGame("Try Borderless Windowed instead of Exclusive Fullscreen, or the other way round."),
                       outGame("Try the other RTSS limiter mode (front-edge sync) and a cap a little below your screen's refresh rate."),
                       outGame("Keep the game on the NVMe drive and give it a few minutes for shaders to build.")];
                break;
        }
        if (part.Length > 0) title = $"{title} ({part})";
        return new AnalysisFinding(kind, cause, episodes, seconds, title, detail, evidence, sug);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Consecutive samples that match, at least <paramref name="min"/> long and no more than <see cref="MaxGapSeconds"/> apart.</summary>
    private static IEnumerable<List<ContextSample>> Runs(List<ContextSample> s, Func<ContextSample, bool> match, int min)
    {
        var run = new List<ContextSample>();
        foreach (var c in s)
        {
            if (match(c) && (run.Count == 0 || (c.At - run[^1].At).TotalSeconds <= MaxGapSeconds)) { run.Add(c); continue; }
            if (run.Count >= min) yield return run;
            run = match(c) ? [c] : [];
        }
        if (run.Count >= min) yield return run;
    }

    private static int Seconds(List<ContextSample> run) => (int)Math.Round((run[^1].At - run[0].At).TotalSeconds) + 2;

    /// <summary>Findings worth telling the player about while they play: not seen before in this session.</summary>
    public static AnalysisFinding? NextLiveNote(SessionAnalysis now, ISet<(AnalysisKind, AnalysisCause)> alreadyShown)
    {
        var next = now.Findings.Where(f => f.Seconds >= 8 || f.Episodes >= 2).FirstOrDefault(f => !alreadyShown.Contains((f.Kind, f.Cause)));
        if (next is not null) alreadyShown.Add((next.Kind, next.Cause));
        return next;
    }
}
