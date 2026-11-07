using System.Text.Json;
using FitzLauncher.Core.Config;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Saves;

namespace FitzLauncher.Core.Progress;

public sealed record AchievementProgress(int Earned, int? Total, string Source)
{
    public double? Percent => Total is > 0 ? Math.Min(100, Earned * 100.0 / Total.Value) : null;
    public string Text => Total is > 0 ? $"{Earned} of {Total} achievements" : $"{Earned} achievements unlocked";
}

/// <summary>
/// Reads achievement progress from the files Steam emulators in repacks keep locally:
/// Goldberg / GSE (achievements.json, "earned": true) and CODEX / RUNE / TENOKE-style
/// achievements.ini (Achieved=1). The total comes from the game's steam_settings/achievements.json
/// when present, otherwise from the caller (e.g. Steam's public achievement count).
/// </summary>
public static class AchievementReader
{
    public static AchievementProgress? Read(Game game, GameScanResult? scan, SaveContext ctx, int? knownTotal = null)
    {
        var appId = scan?.SteamAppId ?? game.SteamAppId;
        var dirs = new List<string>();
        if (appId is { } id)
        {
            var s = id.ToString();
            dirs.AddRange(
            [
                Path.Combine(ctx.RoamingAppData, "Goldberg SteamEmu Saves", s),
                Path.Combine(ctx.RoamingAppData, "GSE Saves", s),
                Path.Combine(ctx.PublicDocuments, "Steam", "CODEX", s),
                Path.Combine(ctx.RoamingAppData, "Steam", "CODEX", s),
                Path.Combine(ctx.PublicDocuments, "Steam", "RUNE", s),
                Path.Combine(ctx.PublicDocuments, "Steam", "TENOKE", s),
                Path.Combine(ctx.RoamingAppData, "EMPRESS", s),
                Path.Combine(ctx.PublicDocuments, "EMPRESS", s),
                Path.Combine(ctx.PublicDocuments, "OnlineFix", s),
            ]);
        }
        dirs.AddRange(game.SaveDirs);

        var total = knownTotal ?? ReadTotal(game.InstallDir);
        foreach (var dir in dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in SafeFiles(dir))
            {
                var name = Path.GetFileName(file).ToLowerInvariant();
                int? earned = name switch
                {
                    "achievements.json" => CountJson(file),
                    "achievements.ini" or "stats.ini" or "achiev.ini" => CountIni(file),
                    _ => null,
                };
                if (earned is { } e) return new AchievementProgress(e, total, Path.GetFileName(Path.GetDirectoryName(file)!) is var src ? src : "");
            }
        }
        return null;
    }

    /// <summary>Goldberg/GSE: {"ACH_1":{"earned":true,...}, ...}</summary>
    public static int CountJson(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return 0;
            return doc.RootElement.EnumerateObject().Count(p =>
                p.Value.ValueKind == JsonValueKind.Object &&
                p.Value.TryGetProperty("earned", out var e) &&
                (e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.Number && e.GetInt32() == 1)));
        }
        catch (Exception e) when (e is IOException or JsonException) { return 0; }
    }

    /// <summary>CODEX/RUNE style: one [Section] per achievement with Achieved=1.</summary>
    public static int CountIni(string path)
    {
        try
        {
            var ini = IniDocument.Load(path);
            return ini.Sections.Count(s => ini.Get(s, "Achieved") is "1" or "true" || ini.Get(s, "achieved") is "1");
        }
        catch (IOException) { return 0; }
    }

    /// <summary>Total from the repack's steam_settings/achievements.json (a JSON array of definitions).</summary>
    public static int? ReadTotal(string installDir)
    {
        if (!Directory.Exists(installDir)) return null;
        var file = SafeFiles(installDir, depth: 4).FirstOrDefault(f =>
            Path.GetFileName(f).Equals("achievements.json", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(f)!.EndsWith("steam_settings", StringComparison.OrdinalIgnoreCase));
        if (file is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    private static IEnumerable<string> SafeFiles(string dir, int depth = 3) =>
        Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true });
}
