using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Emulation;

public enum GpuTier { Integrated, Mid, High }

/// <summary>A rough GPU class from the card's name (used only to decide how high to start the quality ladder).</summary>
public static partial class GpuTierGuess
{
    [GeneratedRegex(@"RTX\s*(30[789]0|40[6789]0|50[6789]0)|RX\s*(6[89]\d0|7[89]\d0|9\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex HighCards();

    [GeneratedRegex(@"RTX|GTX\s*1[06]\d{2}|GTX\s*16\d{2}|RX\s*[567]\d{3}|Radeon\s*80[56]0S|Arc\s*[AB][57]\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex MidCards();

    public static GpuTier Classify(string? name, bool isDiscrete)
    {
        if (string.IsNullOrWhiteSpace(name)) return GpuTier.Integrated;
        if (HighCards().IsMatch(name)) return GpuTier.High;
        if (MidCards().IsMatch(name) || (isDiscrete && !name.Contains("Intel", StringComparison.OrdinalIgnoreCase))) return GpuTier.Mid;
        return GpuTier.Integrated;
    }
}

/// <summary>One rung of the quality ladder: how sharp, how filtered, whether replacement textures load.</summary>
public readonly record struct QualityLevel(int Level, int ResolutionScale, int Anisotropy, bool ReplacementTextures)
{
    public const int Min = 1, Max = 8;
    private static readonly (int scale, int af, bool hires)[] Table =
        [(1, 0, false), (1, 4, true), (2, 4, true), (2, 8, true), (3, 8, true), (3, 16, true), (4, 16, true), (5, 16, true)];

    public static QualityLevel For(int level)
    {
        var l = Math.Clamp(level, Min, Max);
        var (scale, af, hires) = Table[l - 1];
        return new QualityLevel(l, scale, af, hires);
    }

    public string Describe() => $"{ResolutionScale}× resolution, {(Anisotropy == 0 ? "no" : $"{Anisotropy}×")} anisotropic filtering{(ReplacementTextures ? ", custom textures on" : "")}";
}

/// <summary>Where an emulator's settings file can live: next to the program (portable) or in the user's folders.</summary>
public enum ConfigRoot { ExeDir, AppData, Documents }

public sealed record ConfigFileSpec(ConfigFormat Format, (ConfigRoot Root, string Relative)[] Candidates);

public enum TuneKind { Scale, Anisotropy, ReplacementTextures }

public sealed record TuneRule(string File, string[] Path, string Key, TuneKind Kind, Func<QualityLevel, string> Encode);

/// <summary>What the tuner may change for one emulator. Keys come from memory of each emulator's files: the patcher only edits ones that exist (SPEC.md §5.3).</summary>
public sealed record EmulatorTuneMap(Dictionary<string, ConfigFileSpec> Files, List<TuneRule> Rules);

public static class EmuTuneMaps
{
    private static string Log2Af(int af) => af switch { 0 => "0", 2 => "1", 4 => "2", 8 => "3", _ => "4" };
    private static string Bool(bool b) => b ? "true" : "false";

    public static EmulatorTuneMap? For(string emulatorId) => Maps.GetValueOrDefault(emulatorId);

    private static readonly Dictionary<string, EmulatorTuneMap> Maps = new()
    {
        ["pcsx2"] = new(
            new() { ["ini"] = new(ConfigFormat.Ini, [(ConfigRoot.ExeDir, @"inis\PCSX2.ini"), (ConfigRoot.Documents, @"PCSX2\inis\PCSX2.ini")]) },
            [
                new("ini", ["EmuCore/GS"], "upscale_multiplier", TuneKind.Scale, q => q.ResolutionScale.ToString()),
                new("ini", ["EmuCore/GS"], "MaxAnisotropy", TuneKind.Anisotropy, q => q.Anisotropy.ToString()),
                new("ini", ["EmuCore/GS"], "LoadTextureReplacements", TuneKind.ReplacementTextures, q => Bool(q.ReplacementTextures)),
            ]),
        ["dolphin"] = new(
            new() { ["gfx"] = new(ConfigFormat.Ini, [(ConfigRoot.ExeDir, @"User\Config\GFX.ini"), (ConfigRoot.Documents, @"Dolphin Emulator\Config\GFX.ini"), (ConfigRoot.AppData, @"Dolphin Emulator\Config\GFX.ini")]) },
            [
                new("gfx", ["Settings"], "InternalResolution", TuneKind.Scale, q => q.ResolutionScale.ToString()),
                new("gfx", ["Enhancements"], "MaxAnisotropy", TuneKind.Anisotropy, q => Log2Af(q.Anisotropy)),
                new("gfx", ["Settings"], "HiresTextures", TuneKind.ReplacementTextures, q => Bool(q.ReplacementTextures)),
            ]),
        ["rpcs3"] = new(
            new() { ["cfg"] = new(ConfigFormat.Yaml, [(ConfigRoot.ExeDir, @"config\config.yml")]) },
            [
                new("cfg", ["Video"], "Resolution Scale", TuneKind.Scale, q => (q.ResolutionScale * 100).ToString()),
                new("cfg", ["Video"], "Anisotropic Filter Override", TuneKind.Anisotropy, q => q.Anisotropy.ToString()),
            ]),
        ["ppsspp"] = new(
            new() { ["ini"] = new(ConfigFormat.Ini, [(ConfigRoot.ExeDir, @"memstick\PSP\SYSTEM\ppsspp.ini"), (ConfigRoot.Documents, @"PPSSPP\PSP\SYSTEM\ppsspp.ini")]) },
            [
                new("ini", ["Graphics"], "InternalResolution", TuneKind.Scale, q => q.ResolutionScale.ToString()),
                new("ini", ["Graphics"], "AnisotropyLevel", TuneKind.Anisotropy, q => Log2Af(q.Anisotropy)),
                new("ini", ["Graphics"], "ReplaceTextures", TuneKind.ReplacementTextures, q => Bool(q.ReplacementTextures)),
            ]),
        ["lime3ds"] = Qt3ds("Lime3DS"),
        ["azahar"] = Qt3ds("Azahar"),
        ["xenia-canary"] = new(
            new() { ["toml"] = new(ConfigFormat.Toml, [(ConfigRoot.ExeDir, "xenia-canary.config.toml")]) },
            [
                new("toml", ["GPU"], "draw_resolution_scale_x", TuneKind.Scale, q => q.ResolutionScale.ToString()),
                new("toml", ["GPU"], "draw_resolution_scale_y", TuneKind.Scale, q => q.ResolutionScale.ToString()),
            ]),
        ["ryubing"] = new(
            new() { ["json"] = new(ConfigFormat.Json, [(ConfigRoot.ExeDir, @"portable\Config.json"), (ConfigRoot.AppData, @"Ryujinx\Config.json")]) },
            [
                new("json", [], "res_scale", TuneKind.Scale, q => q.ResolutionScale.ToString()),
                new("json", [], "max_anisotropy", TuneKind.Anisotropy, q => q.Anisotropy == 0 ? "-1" : q.Anisotropy.ToString()),
            ]),
    };

    private static EmulatorTuneMap Qt3ds(string folder) => new(
        new() { ["qt"] = new(ConfigFormat.Ini, [(ConfigRoot.ExeDir, @"user\config\qt-config.ini"), (ConfigRoot.AppData, $@"{folder}\config\qt-config.ini")]) },
        [
            new("qt", ["Renderer"], "resolution_factor", TuneKind.Scale, q => q.ResolutionScale.ToString()),
            new("qt", ["Utility"], "custom_textures", TuneKind.ReplacementTextures, q => Bool(q.ReplacementTextures)),
        ]);

    /// <summary>The edits for one file at a quality level.</summary>
    public static List<ConfigEdit> EditsFor(EmulatorTuneMap map, string file, QualityLevel level) =>
        [.. map.Rules.Where(r => r.File == file).Select(r => new ConfigEdit(r.Path, r.Key, r.Encode(level)))];

    /// <summary>The first settings file that exists, or null.</summary>
    public static string? Locate(ConfigFileSpec spec, string exeDir, string appData, string documents, Func<string, bool> exists)
    {
        foreach (var (root, rel) in spec.Candidates)
        {
            var baseDir = root switch { ConfigRoot.ExeDir => exeDir, ConfigRoot.AppData => appData, _ => documents };
            var full = Path.Combine(baseDir, rel.Replace('\\', Path.DirectorySeparatorChar));
            if (exists(full)) return full;
        }
        return null;
    }
}

/// <summary>Per-game tuning memory.</summary>
public sealed class EmuTuningState
{
    public int Level { get; set; }
    /// <summary>Highest level that held the target.</summary>
    public int BestHeld { get; set; }
    /// <summary>Lowest level that did not hold it; not tried again until retry time.</summary>
    public int? FailedLevel { get; set; }
    public int HeldSessionsSinceFail { get; set; }
    public string? LastNote { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}

public enum EmulatorCost { Light, Medium, Heavy }

/// <summary>Chooses where the ladder starts and moves it from what a session measured against the FPS target.</summary>
public static class EmuTuner
{
    public static EmulatorCost CostOf(EmuPlatform p) => p switch
    {
        EmuPlatform.PSP or EmuPlatform.N64 or EmuPlatform.GameCube or EmuPlatform.Wii => EmulatorCost.Light,
        EmuPlatform.PlayStation2 or EmuPlatform.Nintendo3DS => EmulatorCost.Medium,
        _ => EmulatorCost.Heavy,
    };

    /// <summary>The most this PC should attempt: the ladder never goes above it, however well a session ran.</summary>
    public static int Ceiling(GpuTier tier, EmuPlatform p) => (tier, CostOf(p)) switch
    {
        (GpuTier.High, EmulatorCost.Light) => 8,
        (GpuTier.High, EmulatorCost.Medium) => 7,
        (GpuTier.High, _) => 5,
        (GpuTier.Mid, EmulatorCost.Light) => 7,
        (GpuTier.Mid, EmulatorCost.Medium) => 5,
        (GpuTier.Mid, _) => 3,
        (_, EmulatorCost.Light) => 5,
        (_, EmulatorCost.Medium) => 3,
        _ => 2,
    };

    public static int StartLevel(GpuTier tier, EmuPlatform p) => Math.Max(QualityLevel.Min, Ceiling(tier, p) - 1);

    public sealed record Result(int Level, string Note, bool Changed);

    /// <summary>Held = average within 3% of the target and the 1% low at least 80% of it.</summary>
    public static bool Held(double avgFps, double onePercentLow, int target) => target > 0 && avgFps >= target * 0.97 && onePercentLow >= target * 0.80;

    public static Result Adjust(EmuTuningState state, int ceiling, double avgFps, double onePercentLow, double? gpuLoad, int target, TimeSpan played)
    {
        var level = Math.Clamp(state.Level == 0 ? Math.Max(1, ceiling - 1) : state.Level, QualityLevel.Min, ceiling);
        if (played < TimeSpan.FromMinutes(3)) return new(level, "Too short a session to judge. Settings kept.", false);

        if (Held(avgFps, onePercentLow, target))
        {
            state.BestHeld = Math.Max(state.BestHeld, level);
            state.HeldSessionsSinceFail++;
            if (state.FailedLevel is not null && state.HeldSessionsSinceFail >= 5) state.FailedLevel = null; // try the higher level again
            var next = level + 1;
            var headroom = gpuLoad is null or <= 88;
            if (next <= ceiling && headroom && (state.FailedLevel is null || next < state.FailedLevel))
            {
                state.Level = next;
                return new(next, $"Held {target} FPS with room to spare, so quality goes up: {QualityLevel.For(next).Describe()}.", true);
            }
            state.Level = level;
            return new(level, $"Held {target} FPS at the best quality this PC should attempt ({QualityLevel.For(level).Describe()}).", false);
        }

        state.FailedLevel = state.FailedLevel is { } f ? Math.Min(f, level) : level;
        state.HeldSessionsSinceFail = 0;
        if (level <= QualityLevel.Min)
        {
            state.Level = QualityLevel.Min;
            return new(QualityLevel.Min, $"Couldn't hold {target} FPS even at the lowest quality. Try a lower FPS target or another emulator.", false);
        }
        var drop = avgFps < target * 0.75 ? 2 : 1;
        var lower = Math.Max(QualityLevel.Min, level - drop);
        state.Level = lower;
        return new(lower, $"Missed {target} FPS (average {avgFps:0}, 1% low {onePercentLow:0}), so quality goes down: {QualityLevel.For(lower).Describe()}.", true);
    }
}
