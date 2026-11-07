using System.Text.Json;

namespace FitzLauncher.Core.Storage;

/// <summary>
/// Keeps library, settings, play history, benchmarks, presets and artwork in a sync folder (the Google
/// Drive for desktop "My Drive" folder by default), so a Windows reinstall or another PC carries on where
/// you left off. Last writer wins; whatever gets replaced is copied aside first, so nothing is lost.
/// </summary>
public sealed class LibrarySync(AppPaths paths, string syncRoot)
{
    public const string FolderName = "Fitz Launcher sync";
    private const string ManifestName = "manifest.json";

    public sealed record Manifest(DateTimeOffset Updated, string Machine, string AppVersion);

    public string Folder => Path.Combine(syncRoot, FolderName);
    private string[] DataFiles => [paths.Library, paths.Settings, paths.Sessions, paths.Benchmarks, paths.Presets];

    public Manifest? Remote()
    {
        var p = Path.Combine(Folder, ManifestName);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p), Json.Options); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    /// <summary>Local data changed after the last sync if any data file is newer than the stamp.</summary>
    public DateTimeOffset LocalUpdated() =>
        DataFiles.Where(File.Exists).Select(f => (DateTimeOffset)File.GetLastWriteTimeUtc(f)).DefaultIfEmpty(DateTimeOffset.MinValue).Max();

    /// <summary>True when the sync folder holds newer data from another PC (or from before a reinstall).</summary>
    public bool RemoteIsNewer(string machine)
    {
        var r = Remote();
        return r is not null && r.Machine != machine && r.Updated > LocalUpdated().AddSeconds(5);
    }

    public void Upload(string machine, string version)
    {
        Directory.CreateDirectory(Folder);
        foreach (var f in DataFiles.Where(File.Exists)) CopyAtomic(f, Path.Combine(Folder, Path.GetFileName(f)));
        MirrorDir(paths.ArtworkDir, Path.Combine(Folder, "artwork"));
        File.WriteAllText(Path.Combine(Folder, ManifestName),
            JsonSerializer.Serialize(new Manifest(DateTimeOffset.UtcNow, machine, version), Json.Options));
    }

    /// <summary>Copies the synced data over local data. Local files are kept in backups\before-sync first.</summary>
    public void Download()
    {
        var aside = Path.Combine(paths.BackupDir, "before-sync", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(aside);
        foreach (var local in DataFiles)
        {
            var remote = Path.Combine(Folder, Path.GetFileName(local));
            if (!File.Exists(remote)) continue;
            if (File.Exists(local)) File.Copy(local, Path.Combine(aside, Path.GetFileName(local)), overwrite: true);
            CopyAtomic(remote, local);
        }
        MirrorDir(Path.Combine(Folder, "artwork"), paths.ArtworkDir);
    }

    private static void CopyAtomic(string from, string to)
    {
        var tmp = to + ".sync-tmp";
        File.Copy(from, tmp, overwrite: true);
        File.Move(tmp, to, overwrite: true);
    }

    /// <summary>Adds/updates files only (never deletes), so a half-synced folder can't wipe artwork.</summary>
    private static void MirrorDir(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(f));
            if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(f).Length) File.Copy(f, dest, overwrite: true);
        }
    }
}
