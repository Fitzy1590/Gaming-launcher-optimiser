using System.Text.RegularExpressions;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Emulation;

public sealed record FoundEmulator(string Id, string Path);
public sealed record FoundRom(string Path, string Title, bool IsFolder);

public static partial class EmulatorDiscovery
{
    private static readonly string[] Skip = ["$recycle.bin", "system volume information", "windows", "winsxs", "node_modules", ".git", "temp"];

    /// <summary>Looks for each emulator's program under the given folders (a few levels deep, shallowest match wins).</summary>
    public static List<FoundEmulator> Scan(IEnumerable<string> roots, IReadOnlyList<EmulatorSource> sources, int maxDepth = 3)
    {
        var wanted = sources.SelectMany(s => s.ExeNames.Select(e => (exe: e, id: s.Id))).ToLookup(x => x.exe, StringComparer.OrdinalIgnoreCase);
        var best = new Dictionary<string, (string path, int depth)>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            Walk(root, 0, maxDepth, wanted, best);
        return [.. best.Select(kv => new FoundEmulator(kv.Key, kv.Value.path)).OrderBy(f => f.Id)];
    }

    private static void Walk(string dir, int depth, int maxDepth, ILookup<string, (string exe, string id)> wanted, Dictionary<string, (string, int)> best)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.exe"))
                foreach (var (_, id) in wanted[System.IO.Path.GetFileName(file)])
                    if (!best.TryGetValue(id, out var cur) || depth < cur.Item2) best[id] = (file, depth);
            if (depth >= maxDepth) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!Skip.Contains(System.IO.Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) Walk(sub, depth + 1, maxDepth, wanted, best);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
    }

    /// <summary>Game files of one platform under a folder (PS3 game folders count as one game each).</summary>
    public static List<FoundRom> ScanRoms(string folder, EmuPlatform platform, int maxDepth = 4)
    {
        var info = Platforms.Get(platform);
        var found = new List<FoundRom>();
        if (!Directory.Exists(folder)) return found;
        RomWalk(folder, 0, maxDepth, info, found);
        return [.. found.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(f => f.Title)];
    }

    private static void RomWalk(string dir, int depth, int maxDepth, PlatformInfo info, List<FoundRom> found)
    {
        try
        {
            if (info.Platform == EmuPlatform.PlayStation3 && File.Exists(System.IO.Path.Combine(dir, "PS3_GAME", "USRDIR", "EBOOT.BIN")))
            { found.Add(new FoundRom(dir, RomNames.Clean(System.IO.Path.GetFileName(dir)), true)); return; }
            foreach (var file in Directory.EnumerateFiles(dir))
                if (info.Extensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    found.Add(new FoundRom(file, RomNames.Clean(System.IO.Path.GetFileNameWithoutExtension(file)), false));
            if (depth >= maxDepth) return;
            foreach (var sub in Directory.EnumerateDirectories(dir)) RomWalk(sub, depth + 1, maxDepth, info, found);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
    }
}

public static partial class RomNames
{
    [GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Tags();

    /// <summary>"Game_Name (USA) [!].iso" → "Game Name".</summary>
    public static string Clean(string fileName)
    {
        var s = Tags().Replace(fileName, " ").Replace('_', ' ').Replace('.', ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-');
        return s.Length > 0 ? s : fileName;
    }
}

/// <summary>Turns a game file into a library entry that runs through the normal launch pipeline (FPS tracking, overlay, power).</summary>
public static class EmulationLibrary
{
    public static string BuildArguments(EmulatorSource source, string romPath) => source.ArgsTemplate.Replace("{rom}", romPath);

    public static Game Create(FoundRom rom, EmuPlatform platform, EmulatorSource source, string exePath) => Point(new Game
    {
        Name = rom.Title,
        Platform = platform,
        RomPath = rom.Path,
        FpsTarget = Platforms.DefaultTarget(platform),
        Status = PlayStatus.Backlog,
    }, source, exePath);

    /// <summary>Points an existing game at an emulator (also used when switching emulators).</summary>
    public static Game Point(Game game, EmulatorSource source, string exePath)
    {
        game.EmulatorId = source.Id;
        game.ExePath = exePath;
        game.InstallDir = Path.GetDirectoryName(exePath) ?? "";
        game.Arguments = BuildArguments(source, game.RomPath ?? "");
        game.UseGameMode = true;
        return game;
    }
}
