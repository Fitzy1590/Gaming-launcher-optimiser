using System.Globalization;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Scanning;

namespace FitzLauncher.Core.Optimization;

/// <summary>What to do for the screen that's active at launch.</summary>
public sealed class DisplayPlan
{
    public required DisplayInfo Display { get; init; }
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    /// <summary>Render scale percent; 100 = native. Only real on engines with a render-scale setting.</summary>
    public int RenderScale { get; init; } = 100;
    /// <summary>The frame cap for this screen (lowered when the screen can't show the game's target).</summary>
    public int? EffectiveCap { get; init; }
    /// <summary>Config edits (Unreal), applied through the tweak engine so they stay revertible.</summary>
    public List<TweakAction> Actions { get; init; } = [];
    /// <summary>One-launch options (Unity/Source) that set the resolution without touching files.</summary>
    public string LaunchArguments { get; init; } = "";
    public bool CanSetResolution { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// Picks resolution and render scale for whichever screen is active when a game starts, so switching
/// between the handheld panel and a widescreen monitor just works. Output stays at the screen's native
/// resolution for sharpness; to reach the FPS target it lowers the render scale (upscaled by the engine's
/// TSR/FSR), first estimated from FPS on other screens by pixel count, then learned per screen after each session.
/// </summary>
public static class DisplayPlanner
{
    public const string TweakId = "auto-display";
    public static readonly int[] ScaleLadder = [100, 90, 83, 77, 67, 59, 50];

    public static DisplayPlan Plan(Game game, GameScanResult? scan, DisplayInfo display, IEnumerable<SessionSummary> history, bool allowLowerOutput)
    {
        var cap = EffectiveCap(game.FpsTarget, display.MaxRefreshHz);
        var target = cap ?? 0;
        var engine = scan?.Engine ?? GameEngine.Unknown;
        var isUnreal = engine is GameEngine.Unreal4 or GameEngine.Unreal5 or GameEngine.UnrealUnknownVersion;
        var supportsScale = isUnreal && OptimizationPlanner.UnrealConfigDir(scan!) is not null;

        // Learned value for this screen wins; otherwise estimate from other screens.
        var scale = game.DisplayProfiles.TryGetValue(display.Key, out var profile) ? profile.RenderScale
                  : target > 0 ? EstimateScale(display, target, history) : 100;

        int outW = display.Width, outH = display.Height;
        var actions = new List<TweakAction>();
        var args = "";
        string how;

        if (supportsScale)
        {
            var dir = OptimizationPlanner.UnrealConfigDir(scan!)!;
            var gus = Path.Combine(dir, "GameUserSettings.ini");
            IniSetAction Set(string section, string key, string value) => new() { FilePath = gus, Section = section, Key = key, Value = value, CreateIfMissing = false };
            var section = UnrealKeys.GameUserSettingsSection;
            actions.AddRange(
            [
                Set(section, "ResolutionSizeX", outW.ToString(CultureInfo.InvariantCulture)),
                Set(section, "ResolutionSizeY", outH.ToString(CultureInfo.InvariantCulture)),
                Set(section, "LastUserConfirmedResolutionSizeX", outW.ToString(CultureInfo.InvariantCulture)),
                Set(section, "LastUserConfirmedResolutionSizeY", outH.ToString(CultureInfo.InvariantCulture)),
                Set("ScalabilityGroups", "sg.ResolutionQuality", scale.ToString(CultureInfo.InvariantCulture)),
            ]);
            how = scale < 100 ? $"native {outW}×{outH}, rendering at {scale}% and upscaled by the engine" : $"native {outW}×{outH}";
        }
        else
        {
            if (allowLowerOutput && scale < 100)
            {
                (outW, outH) = Scaled(display.Width, display.Height, scale);
            }
            else scale = 100;

            args = engine switch
            {
                GameEngine.Unity => $"-screen-width {outW} -screen-height {outH}",
                GameEngine.Source or GameEngine.Source2 => $"-w {outW} -h {outH}",
                _ => "",
            };
            how = args.Length > 0 ? $"{outW}×{outH} via launch options" : "resolution set in-game (this engine can't be set from outside)";
        }

        var capText = cap is { } c ? (game.FpsTarget is { } t && c < t ? $", cap {c} (screen is {display.MaxRefreshHz} Hz)" : $", cap {c}") : "";
        return new DisplayPlan
        {
            Display = display, OutputWidth = outW, OutputHeight = outH, RenderScale = scale, EffectiveCap = cap,
            Actions = actions, LaunchArguments = args, CanSetResolution = actions.Count > 0 || args.Length > 0,
            Summary = $"{display.Label}: {how}{capText}",
        };
    }

    /// <summary>The cap to use on this screen: the game's target, or just under the refresh rate if lower.</summary>
    public static int? EffectiveCap(int? target, int refreshHz)
    {
        if (target is not { } t || refreshHz <= 0 || t <= refreshHz - 3) return target;
        var allowed = LauncherSettings.FpsTargets.Where(x => x <= refreshHz - 3).ToList();
        return allowed.Count > 0 ? allowed.Max() : refreshHz - 3; // e.g. 57 on a 60 Hz TV
    }

    /// <summary>
    /// Predicts FPS here from the best session on another screen: GPU-bound FPS scales roughly with
    /// rendered pixels (exponent 0.8 to allow for CPU-bound parts). Picks the highest scale with headroom.
    /// </summary>
    public static int EstimateScale(DisplayInfo display, int target, IEnumerable<SessionSummary> history)
    {
        var reference = history.Where(s => s.HasFpsData && s.DisplayWidth > 0 && s.DisplayHeight > 0)
                               .OrderByDescending(s => s.End).FirstOrDefault();
        if (reference is null) return 100;
        var refPixels = (double)reference.DisplayWidth * reference.DisplayHeight * Math.Pow(reference.RenderScale / 100.0, 2);
        foreach (var s in ScaleLadder)
        {
            var pixels = (double)display.Width * display.Height * Math.Pow(s / 100.0, 2);
            var predicted = reference.AverageFps * Math.Pow(refPixels / pixels, 0.8);
            if (predicted >= target * 1.12) return s; // ~12% headroom so the 1% lows hold too
        }
        return ScaleLadder[^1];
    }

    /// <summary>After a session: step this screen's render scale down if the target was missed, up if there's headroom.</summary>
    public static string? Learn(Game game, SessionSummary s, int? cap)
    {
        if (s.DisplayKey is null || !s.HasFpsData || s.Duration.TotalMinutes < 3) return null;
        if (!game.DisplayProfiles.TryGetValue(s.DisplayKey, out var p))
            game.DisplayProfiles[s.DisplayKey] = p = new DisplayProfile { RenderScale = s.RenderScale };
        p.Label = $"{s.DisplayWidth}×{s.DisplayHeight}";
        p.LastAvgFps = s.AverageFps;
        p.LastLowFps = s.OnePercentLowFps;
        p.Sessions++;
        p.LastPlayed = s.End;
        if (cap is not { } target) return null;

        var i = Array.IndexOf(ScaleLadder, Nearest(p.RenderScale));
        var missed = (s.TimeAtTarget ?? 1) < 0.9 || s.OnePercentLowFps < target * 0.9;
        var headroom = (s.TimeAtTarget ?? 0) >= 0.98 && s.OnePercentLowFps >= target * 1.0 && s.AverageFps >= target * 0.99;
        if (missed && i < ScaleLadder.Length - 1)
        {
            p.RenderScale = ScaleLadder[i + 1];
            return $"Render scale on {p.Label} lowered to {p.RenderScale}% for next time (missed {target} FPS).";
        }
        if (headroom && i > 0 && s.OnePercentLowFps >= target * 1.0 && LastScaleWasLowered(p))
        {
            p.RenderScale = ScaleLadder[i - 1];
            return $"Render scale on {p.Label} raised to {p.RenderScale}%: {target} FPS held with room to spare.";
        }
        return null;
    }

    // Raising only happens after a step that held the target, to avoid ping-ponging between two scales.
    private static bool LastScaleWasLowered(DisplayProfile p) => p.RenderScale < 100;

    private static int Nearest(int scale) => ScaleLadder.OrderBy(s => Math.Abs(s - scale)).First();

    private static (int w, int h) Scaled(int w, int h, int scale) =>
        ((int)Math.Round(w * scale / 100.0 / 2) * 2, (int)Math.Round(h * scale / 100.0 / 2) * 2);
}
