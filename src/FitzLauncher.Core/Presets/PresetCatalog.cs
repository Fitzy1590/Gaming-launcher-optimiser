using System.Text.Json;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Core.Presets;

/// <summary>One known-good config fix for a specific game.</summary>
public sealed class GamePreset
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Matches when the game's exe name (any of these) is present.</summary>
    public List<string> ExeNames { get; set; } = [];
    /// <summary>…or the Unreal project name / game name contains one of these.</summary>
    public List<string> NameContains { get; set; } = [];
    public VisualImpact VisualImpact { get; set; }
    public TweakRisk Risk { get; set; } = TweakRisk.Safe;
    public string ExpectedGain { get; set; } = "";
    public string? Source { get; set; }
    public List<PresetSetting> Settings { get; set; } = [];
    public string? LaunchArguments { get; set; }
}

/// <summary>A setting with a path token: {ConfigDir} (detected config folder) or {InstallDir}.</summary>
public sealed class PresetSetting
{
    public string File { get; set; } = "{ConfigDir}\\Engine.ini";
    public string Section { get; set; } = "SystemSettings";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// Per-game presets. They live in <c>presets.json</c> in the data folder (yours to edit or share),
/// plus any built-ins. Each preset becomes a normal suggestion, so it's shown with its impact,
/// applied with a backup and revertible like everything else.
/// </summary>
public static class PresetCatalog
{
    /// <summary>
    /// Deliberately empty: I'd rather ship no preset than one I can't verify for your exact game
    /// versions. Add your own to presets.json, or send me your game list and I'll research them.
    /// </summary>
    public static readonly List<GamePreset> BuiltIn = [];

    public static List<GamePreset> Load(string presetsFile)
    {
        var list = new List<GamePreset>(BuiltIn);
        if (!File.Exists(presetsFile))
        {
            WriteExample(presetsFile);
            return list;
        }
        try
        {
            var user = JsonSerializer.Deserialize<List<GamePreset>>(File.ReadAllText(presetsFile), Json.Options);
            if (user is not null) list.AddRange(user.Where(p => p.Id.Length > 0 && !p.Id.StartsWith("example", StringComparison.OrdinalIgnoreCase)));
        }
        catch (JsonException) { /* a typo in the user's file shouldn't break the launcher */ }
        return list;
    }

    public static IEnumerable<Tweak> For(Game game, GameScanResult scan, IEnumerable<GamePreset> presets)
    {
        var exe = game.ExeName;
        foreach (var p in presets)
        {
            var match = p.ExeNames.Any(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase)) ||
                        p.NameContains.Any(n => game.Name.Contains(n, StringComparison.OrdinalIgnoreCase) ||
                                                (scan.UnrealProjectName?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false));
            if (!match) continue;

            var configDir = scan.ConfigDirectories.FirstOrDefault();
            var actions = new List<TweakAction>();
            var ok = true;
            foreach (var s in p.Settings)
            {
                if (s.File.Contains("{ConfigDir}") && configDir is null) { ok = false; break; }
                var file = s.File.Replace("{ConfigDir}", configDir ?? "").Replace("{InstallDir}", game.InstallDir)
                                 .Replace('\\', Path.DirectorySeparatorChar);
                actions.Add(new IniSetAction { FilePath = file, Section = s.Section, Key = s.Key, Value = s.Value });
            }
            if (!ok) continue;
            if (!string.IsNullOrWhiteSpace(p.LaunchArguments)) actions.Add(new LaunchArgumentAction { Arguments = p.LaunchArguments });
            if (actions.Count == 0) continue;

            yield return new Tweak
            {
                Id = "preset-" + p.Id,
                Title = $"Preset: {p.Title}",
                Description = p.Description + (p.Source is { Length: > 0 } src ? $" (Source: {src})" : ""),
                Category = TweakCategory.Stutter,
                VisualImpact = p.VisualImpact,
                Risk = p.Risk,
                ExpectedGain = p.ExpectedGain,
                Actions = actions,
                Recommended = true,
                Reason = "Known fix for this game.",
                OneClickEligible = p.VisualImpact == VisualImpact.None && p.Risk == TweakRisk.Safe,
            };
        }
    }

    private static void WriteExample(string path)
    {
        var example = new List<GamePreset>
        {
            new()
            {
                Id = "example-my-game",
                Title = "Example: remove the 'example-' prefix from Id to enable",
                Description = "Template: copy this block per game.",
                ExeNames = ["MyGame-Win64-Shipping.exe"],
                NameContains = ["My Game"],
                VisualImpact = VisualImpact.None,
                ExpectedGain = "Fewer hitches",
                Settings = [new PresetSetting { Key = "r.Streaming.PoolSize", Value = "4096" }],
            },
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(example, Json.Options));
        }
        catch (IOException) { }
    }
}
