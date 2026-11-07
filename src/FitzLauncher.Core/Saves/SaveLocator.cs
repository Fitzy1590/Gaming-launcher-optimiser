using FitzLauncher.Core.Models;
using FitzLauncher.Core.Scanning;

namespace FitzLauncher.Core.Saves;

/// <summary>User folders where saves live. Injected for testability.</summary>
public sealed record SaveContext(string Documents, string SavedGames, string RoamingAppData, string LocalAppData, string LocalLow, string PublicDocuments)
{
    public static SaveContext FromEnvironment()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new SaveContext(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Saved Games"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(Path.GetDirectoryName(local)!, "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments));
    }
}

/// <summary>
/// Finds a game's save folders. Repacks keep saves wherever their Steam emulator puts them
/// (Goldberg, CODEX, RUNE, EMPRESS, OnlineFix…), keyed by the Steam app id, so those are checked
/// first, then the engine's standard locations, then folders named after the game.
/// The user can always add or remove folders.
/// </summary>
public static class SaveLocator
{
    public static List<string> FindCandidates(Game game, GameScanResult? scan, SaveContext ctx)
    {
        var dirs = new List<string>();
        var appId = scan?.SteamAppId ?? game.SteamAppId;
        if (appId is { } id)
        {
            var s = id.ToString();
            dirs.AddRange(
            [
                Path.Combine(ctx.RoamingAppData, "Goldberg SteamEmu Saves", s),
                Path.Combine(ctx.RoamingAppData, "GSE Saves", s),
                Path.Combine(ctx.RoamingAppData, "Steam", "CODEX", s),
                Path.Combine(ctx.PublicDocuments, "Steam", "CODEX", s),
                Path.Combine(ctx.PublicDocuments, "Steam", "RUNE", s),
                Path.Combine(ctx.PublicDocuments, "Steam", "TENOKE", s),
                Path.Combine(ctx.RoamingAppData, "EMPRESS", s),
                Path.Combine(ctx.PublicDocuments, "EMPRESS", s),
                Path.Combine(ctx.PublicDocuments, "OnlineFix", s),
                Path.Combine(ctx.RoamingAppData, "SmartSteamEmu", s),
                Path.Combine(ctx.LocalAppData, "SKIDROW", s),
            ]);
        }

        var names = NameVariants(game, scan);
        if (scan?.UnrealProjectName is { } project)
        {
            dirs.Add(Path.Combine(ctx.LocalAppData, project, "Saved", "SaveGames"));
            dirs.Add(Path.Combine(ctx.Documents, "My Games", project, "Saved", "SaveGames"));
            dirs.Add(Path.Combine(GameScanner.ResolveInstallRoot(game), project, "Saved", "SaveGames"));
        }
        if (scan is { UnityCompany: { } company, UnityProduct: { } product })
            dirs.Add(Path.Combine(ctx.LocalLow, company, product));

        foreach (var n in names)
        {
            dirs.Add(Path.Combine(ctx.SavedGames, n));
            dirs.Add(Path.Combine(ctx.Documents, "My Games", n));
            dirs.Add(Path.Combine(ctx.Documents, n));
            dirs.Add(Path.Combine(ctx.RoamingAppData, n));
            dirs.Add(Path.Combine(ctx.LocalAppData, n, "Saved", "SaveGames"));
        }

        // Portable repacks sometimes keep saves next to the game.
        var root = GameScanner.ResolveInstallRoot(game);
        foreach (var sub in new[] { "Saves", "SaveGames", "Save", "saves", "savegame", "Profiles" })
            dirs.Add(Path.Combine(root, sub));

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase)
                   .Where(d => Directory.Exists(d) && Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any())
                   .ToList();
    }

    private static List<string> NameVariants(Game game, GameScanResult? scan)
    {
        var n = game.Name.Trim();
        var list = new List<string> { n, n.Replace(" ", ""), n.Replace(":", "").Replace("  ", " ") };
        if (scan?.UnrealProjectName is { } p) list.Add(p);
        return list.Where(x => x.Length > 1 && x.IndexOfAny(Path.GetInvalidFileNameChars()) < 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
