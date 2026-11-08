using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Mods;

public sealed record FileConflict(string File, InstalledMod Winner, List<InstalledMod> Overridden);

/// <summary>
/// Copies mods from their staging folders into the game, and back out again. Every game file a mod
/// overwrites is backed up first; disabling the mod puts back the next mod's copy (by priority) or the
/// original. Staging: {staging}/{modId}/{path}. Originals: {backups}/{path}.
/// </summary>
public sealed class ModDeployer(string gameRoot, string stagingRoot, string originalsRoot)
{
    public string StagingFor(InstalledMod mod) => Path.Combine(stagingRoot, mod.Id.ToString("N"));

    private string GamePath(string rel) => Path.Combine(gameRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    private string StagedPath(InstalledMod m, string rel) => Path.Combine(StagingFor(m), rel.Replace('/', Path.DirectorySeparatorChar));
    private string OriginalPath(string rel) => Path.Combine(originalsRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Enabled mod that should provide this file: the highest priority one.</summary>
    private static InstalledMod? Winner(IEnumerable<InstalledMod> mods, string rel, InstalledMod? except = null) =>
        mods.Where(m => m.Enabled && m != except && m.Files.Contains(rel, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Priority).FirstOrDefault();

    /// <summary>Puts an enabled mod's files into the game (where it wins), backing up originals.</summary>
    public List<string> Deploy(InstalledMod mod, IReadOnlyList<InstalledMod> all)
    {
        var errors = new List<string>();
        foreach (var rel in mod.Files)
        {
            if (!ModInstallPlanner.IsSafeRelative(rel)) continue;
            if (Winner(all, rel) is { } w && w != mod) continue; // a higher-priority mod already provides it
            try
            {
                var dest = GamePath(rel);
                var ownedByOtherMod = all.Any(m => m != mod && m.Enabled && m.Files.Contains(rel, StringComparer.OrdinalIgnoreCase));
                if (File.Exists(dest) && !ownedByOtherMod && !File.Exists(OriginalPath(rel)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(OriginalPath(rel))!);
                    File.Copy(dest, OriginalPath(rel));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(StagedPath(mod, rel), dest, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"{rel}: {e.Message}"); }
        }
        return errors;
    }

    /// <summary>
    /// Takes an enabled mod's files out of the game: next mod's copy, else the original, else removed.
    /// Call before setting <see cref="InstalledMod.Enabled"/> to false or removing the mod.
    /// </summary>
    public List<string> Undeploy(InstalledMod mod, IReadOnlyList<InstalledMod> all)
    {
        var errors = new List<string>();
        if (!mod.Enabled) return errors; // nothing of it is in the game
        foreach (var rel in mod.Files)
        {
            if (!ModInstallPlanner.IsSafeRelative(rel)) continue;
            if (Winner(all, rel) is { } w && w != mod) continue; // a higher-priority mod's copy is in place
            try
            {
                var dest = GamePath(rel);
                if (Winner(all, rel, except: mod) is { } next)
                    File.Copy(StagedPath(next, rel), dest, overwrite: true);
                else if (File.Exists(OriginalPath(rel)))
                {
                    File.Copy(OriginalPath(rel), dest, overwrite: true);
                    File.Delete(OriginalPath(rel));
                }
                else if (File.Exists(dest))
                {
                    File.Delete(dest);
                    PruneEmptyDirs(Path.GetDirectoryName(dest)!, gameRoot);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"{rel}: {e.Message}"); }
        }
        return errors;
    }

    /// <summary>Files provided by more than one enabled mod.</summary>
    public static List<FileConflict> Conflicts(IReadOnlyList<InstalledMod> mods)
    {
        var owners = new Dictionary<string, List<InstalledMod>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods.Where(m => m.Enabled))
            foreach (var f in m.Files)
                (owners.TryGetValue(f, out var l) ? l : owners[f] = []).Add(m);
        return owners.Where(kv => kv.Value.Count > 1)
            .Select(kv =>
            {
                var ordered = kv.Value.OrderByDescending(m => m.Priority).ToList();
                return new FileConflict(kv.Key, ordered[0], ordered.Skip(1).ToList());
            }).ToList();
    }

    /// <summary>Whether a mod's staged copy is still there (e.g. after the data folder was cleared).</summary>
    public bool IsStaged(InstalledMod mod) => Directory.Exists(StagingFor(mod));

    private static void PruneEmptyDirs(string dir, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        while (Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) is var full && full.Length > stop.Length && full.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
                Directory.Delete(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            dir = Path.GetDirectoryName(dir)!;
        }
    }
}

/// <summary>Bethesda load list (%LOCALAPPDATA%\{game}\Plugins.txt): new plugins go at the end, enabled.</summary>
public static class PluginsTxt
{
    private static readonly HashSet<string> Official = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "Fallout4.esm", "DLCRobot.esm", "DLCworkshop01.esm",
        "DLCCoast.esm", "DLCworkshop02.esm", "DLCworkshop03.esm", "DLCNukaWorld.esm", "Starfield.esm", "FalloutNV.esm", "Fallout3.esm", "Oblivion.esm",
    };

    public static void Add(string path, IEnumerable<string> plugins, bool starFormat)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        foreach (var p in plugins.Where(p => !Official.Contains(p)))
        {
            if (lines.Any(l => Name(l).Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
            lines.Add(starFormat ? "*" + p : p);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    public static void Remove(string path, IEnumerable<string> plugins)
    {
        if (!File.Exists(path)) return;
        var set = plugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        File.WriteAllLines(path, File.ReadAllLines(path).Where(l => !set.Contains(Name(l))));
    }

    public static List<string> Active(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).Where(l => l.StartsWith('*') || (l.Length > 0 && l[0] != '#')).Select(Name).ToList() : [];

    private static string Name(string line) => line.TrimStart('*').Trim();
}

/// <summary>Reads the master files a Bethesda plugin needs (TES4 header MAST records).</summary>
public static class PluginMasters
{
    public static List<string> Read(string pluginPath)
    {
        var masters = new List<string>();
        try
        {
            using var fs = File.OpenRead(pluginPath);
            using var br = new BinaryReader(fs);
            if (new string(br.ReadChars(4)) != "TES4") return masters;
            var dataSize = br.ReadUInt32();
            // Skyrim/FO4/Starfield headers are 24 bytes, Oblivion 20: detect by the first subrecord tag.
            fs.Position = 24;
            if (new string(br.ReadChars(4)) != "HEDR") fs.Position = 20; else fs.Position = 24;
            var end = fs.Position + dataSize;
            while (fs.Position + 6 <= end)
            {
                var tag = new string(br.ReadChars(4));
                var size = br.ReadUInt16();
                var data = br.ReadBytes(size);
                if (tag == "MAST") masters.Add(System.Text.Encoding.Latin1.GetString(data).TrimEnd('\0'));
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentException) { }
        return masters;
    }
}
