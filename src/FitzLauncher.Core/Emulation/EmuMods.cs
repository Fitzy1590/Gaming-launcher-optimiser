using System.Security.Cryptography;
using System.Text.Json;
using FitzLauncher.Core.Mods;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Core.Emulation;

public enum EmuModKind { Mod, Textures, Patch }

/// <summary>A mod, texture pack or patch the user added for one emulator game. The files live once in Fitz's library; turning it on copies them into the emulator's folder.</summary>
public sealed class EmuModEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public EmuModKind Kind { get; set; }
    /// <summary>Folder inside the mods library holding the unpacked files.</summary>
    public string LibraryDir { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Files written into the emulator while enabled: full path → SHA-256, so turning it off removes exactly those.</summary>
    public Dictionary<string, string> Written { get; set; } = [];
}

public static class ModDestinations
{
    /// <summary>
    /// Folder (relative to the emulator's user folder) a mod of this kind belongs in, or null when it can't be placed (unsupported or ID missing).
    /// Layouts are from memory (SPEC.md §3.2) and are checked on device.
    /// </summary>
    public static string? For(string emulatorId, EmuModKind kind, string? titleId, string modName, out string reason)
    {
        reason = "";
        var safe = string.Concat(modName.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (safe.Length == 0) safe = "mod";
        if (string.IsNullOrEmpty(titleId) && emulatorId is not ("rpcs3" or "xenia-canary"))
        {
            reason = "Fitz couldn't read this game's ID from the file, so it can't tell which folder to use.";
            return null;
        }

        switch (emulatorId, kind)
        {
            case ("lime3ds" or "azahar", EmuModKind.Mod): return $@"load\mods\{titleId}";
            case ("lime3ds" or "azahar", EmuModKind.Textures): return $@"load\textures\{titleId}";
            case ("pcsx2", EmuModKind.Textures): return $@"textures\{titleId}\replacements";
            case ("pcsx2", EmuModKind.Patch): return "patches";
            case ("dolphin", EmuModKind.Mod): return $@"Load\GraphicMods\{safe}";
            case ("dolphin", EmuModKind.Textures): return $@"Load\Textures\{titleId}";
            case ("ppsspp", EmuModKind.Textures): return $@"PSP\TEXTURES\{titleId}";
            case ("rpcs3", EmuModKind.Patch): return "patches";
            case ("xenia-canary", EmuModKind.Patch): return "patches";
            case ("ryubing", EmuModKind.Mod): return $@"mods\contents\{titleId}\{safe}";
            case ("yuzu-fork", EmuModKind.Mod): return $@"load\{titleId}\{safe}";
        }
        reason = $"{kind} files aren't supported for this emulator yet.";
        return null;
    }

    public static EmuModKind[] KindsFor(string emulatorId) => Enum.GetValues<EmuModKind>().Where(k => For(emulatorId, k, "X", "m", out _) is not null).ToArray();
}

public static class EmuModStore
{
    /// <summary>Unpacks an archive (or copies a loose file or folder) into the library.</summary>
    public static void Import(string source, string libraryDir)
    {
        if (Directory.Exists(libraryDir)) Directory.Delete(libraryDir, true);
        if (Directory.Exists(source)) { CopyDir(source, libraryDir); return; }
        if (ModArchive.IsArchive(source)) { EmuArchive.ExtractAll(source, libraryDir); return; }
        Directory.CreateDirectory(libraryDir);
        File.Copy(source, Path.Combine(libraryDir, Path.GetFileName(source)), true);
    }

    public sealed record Result(bool Ok, string Message, List<string> Conflicts);

    /// <summary>Copies the mod's files into <paramref name="destDir"/>. A file that is already there and isn't ours is left alone and reported.</summary>
    public static Result Enable(EmuModEntry mod, string destDir)
    {
        if (mod.Enabled) return new(true, "Already on.", []);
        if (!Directory.Exists(mod.LibraryDir)) return new(false, "The mod's files are missing from Fitz's library.", []);
        var files = Directory.GetFiles(mod.LibraryDir, "*", SearchOption.AllDirectories);
        var root = Path.GetFullPath(destDir);
        var conflicts = new List<string>();
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(mod.LibraryDir, f);
            var target = Path.GetFullPath(Path.Combine(root, rel));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return new(false, "A file in the mod points outside the emulator folder, so it was not installed.", []);
            if (File.Exists(target)) conflicts.Add(rel);
        }
        if (conflicts.Count > 0) return new(false, $"{conflicts.Count} file(s) already exist there (another mod or the game's own). Nothing was changed.", conflicts);

        var written = new Dictionary<string, string>();
        try
        {
            foreach (var f in files)
            {
                var target = Path.GetFullPath(Path.Combine(root, Path.GetRelativePath(mod.LibraryDir, f)));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target);
                written[target] = Hash(target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var w in written.Keys) { try { File.Delete(w); } catch (IOException) { } }
            return new(false, $"Couldn't copy the files: {e.Message}. Nothing was changed.", []);
        }
        mod.Written = written;
        mod.Enabled = true;
        return new(true, $"Turned on ({written.Count} file(s)).", []);
    }

    /// <summary>Removes exactly the files this mod wrote. Files changed since (hash differs) are kept and reported.</summary>
    public static Result Disable(EmuModEntry mod)
    {
        var kept = new List<string>();
        foreach (var (path, hash) in mod.Written)
        {
            try
            {
                if (!File.Exists(path)) continue;
                if (Hash(path) != hash) { kept.Add(path); continue; }
                File.Delete(path);
                PruneEmptyDirs(Path.GetDirectoryName(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { kept.Add(path); }
        }
        mod.Enabled = false;
        mod.Written = [];
        return new(true, kept.Count == 0 ? "Turned off." : $"Turned off. {kept.Count} file(s) were changed since and were left in place.", kept);
    }

    private static void PruneEmptyDirs(string? dir)
    {
        // Only folders the mod itself created are emptied, and never above a folder with other content.
        for (var i = 0; i < 4 && dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any(); i++)
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static string Hash(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), true);
    }
}
