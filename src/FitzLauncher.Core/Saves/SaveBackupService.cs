using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Saves;

public sealed record BackupEntry(string Path, DateTimeOffset Taken, long Bytes);
public sealed record BackupResult(bool Created, string Message, string? ZipPath = null, List<string>? CopiedTo = null);

/// <summary>
/// Zips a game's save folders before every launch (only when something changed), keeps the last N
/// locally and copies each backup to the extra destinations (Google Drive folder, USB drive, NAS).
/// Destinations that are offline (USB unplugged) catch up automatically next time.
/// </summary>
public sealed class SaveBackupService(string localRoot)
{
    private const string ManifestName = "fitz-backup.json";
    public const string DestinationFolder = "Fitz Launcher save backups";

    private sealed record Manifest(string Game, Guid GameId, DateTimeOffset Taken, List<string> Folders);

    public string GameFolder(Game game) => Path.Combine(localRoot, FolderName(game));

    public static string FolderName(Game game)
    {
        var safe = new string(game.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return $"{safe} [{game.Id.ToString("N")[..8]}]";
    }

    /// <summary>Cheap change detector: hash of every file's relative path, size and modified time.</summary>
    public static string Fingerprint(IEnumerable<string> dirs)
    {
        var sb = new StringBuilder();
        foreach (var dir in dirs.Where(Directory.Exists).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var fi = new FileInfo(f);
            sb.Append(f).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <param name="destinations">Resolved, currently available destination folders.</param>
    public BackupResult Backup(Game game, IReadOnlyList<string> destinations, int keep, bool force = false)
    {
        var dirs = game.SaveDirs.Where(Directory.Exists).ToList();
        if (dirs.Count == 0) return new BackupResult(false, "No save folder set for this game.");

        var fingerprint = Fingerprint(dirs);
        string? zip = null;
        if (force || fingerprint != game.LastSaveFingerprint)
        {
            var folder = GameFolder(game);
            Directory.CreateDirectory(folder);
            var now = DateTimeOffset.Now;
            zip = Path.Combine(folder, $"{now:yyyy-MM-dd_HH-mm-ss}.zip");
            var tmp = zip + ".tmp";
            using (var archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                for (var i = 0; i < dirs.Count; i++)
                {
                    foreach (var f in Directory.EnumerateFiles(dirs[i], "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(dirs[i], f).Replace('\\', '/');
                        try
                        {
                            // Open with sharing so a running game's lock doesn't break the backup.
                            using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var entry = archive.CreateEntry($"{i}/{rel}", CompressionLevel.Optimal);
                            entry.LastWriteTime = File.GetLastWriteTime(f);
                            using var dst = entry.Open();
                            src.CopyTo(dst);
                        }
                        catch (IOException) { /* file vanished or is exclusively locked; skip it */ }
                    }
                }
                var manifest = archive.CreateEntry(ManifestName);
                using var ms = manifest.Open();
                JsonSerializer.Serialize(ms, new Manifest(game.Name, game.Id, now, dirs));
            }
            File.Move(tmp, zip, overwrite: true);
            game.LastSaveFingerprint = fingerprint;
            game.LastSaveBackup = now;
            Rotate(folder, keep);
        }

        var copied = SyncDestinations(game, destinations, keep);
        return zip is null
            ? new BackupResult(false, copied.Count > 0 ? $"Saves unchanged; synced to {string.Join(", ", copied)}" : "Saves unchanged since the last backup.", null, copied)
            : new BackupResult(true, $"Backed up saves ({new FileInfo(zip).Length / 1024.0:0} KB)" + (copied.Count > 0 ? $"; copied to {string.Join(", ", copied)}" : ""), zip, copied);
    }

    /// <summary>Copies any local backups missing from each destination (catch-up after a USB was unplugged).</summary>
    public List<string> SyncDestinations(Game game, IReadOnlyList<string> destinations, int keep)
    {
        var copied = new List<string>();
        var local = List(game).Take(keep).ToList();
        foreach (var dest in destinations)
        {
            try
            {
                var target = Path.Combine(dest, DestinationFolder, FolderName(game));
                Directory.CreateDirectory(target);
                var any = false;
                foreach (var b in local)
                {
                    var to = Path.Combine(target, Path.GetFileName(b.Path));
                    if (File.Exists(to) && new FileInfo(to).Length == b.Bytes) continue;
                    File.Copy(b.Path, to + ".tmp", overwrite: true);
                    File.Move(to + ".tmp", to, overwrite: true);
                    any = true;
                }
                Rotate(target, keep);
                if (any) copied.Add(dest);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* destination went away mid-copy */ }
        }
        return copied;
    }

    public List<BackupEntry> List(Game game)
    {
        var folder = GameFolder(game);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupEntry(f.FullName, f.LastWriteTime, f.Length))
            .ToList();
    }

    /// <summary>Restores a backup over the save folders. The current saves are backed up first, so a restore can be undone.</summary>
    public BackupResult Restore(Game game, string zipPath, IReadOnlyList<string> destinations, int keep)
    {
        var safety = Backup(game, destinations, keep + 1, force: true);
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = archive.GetEntry(ManifestName);
        var folders = manifestEntry is null ? game.SaveDirs
            : JsonSerializer.Deserialize<Manifest>(manifestEntry.Open())?.Folders ?? game.SaveDirs;

        var restored = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == ManifestName || entry.FullName.EndsWith('/')) continue;
            var slash = entry.FullName.IndexOf('/');
            if (slash <= 0 || !int.TryParse(entry.FullName[..slash], out var index) || index >= folders.Count) continue;
            var root = Path.GetFullPath(folders[index]);
            var dest = Path.GetFullPath(Path.Combine(root, entry.FullName[(slash + 1)..]));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            restored++;
        }
        game.LastSaveFingerprint = Fingerprint(game.SaveDirs);
        return new BackupResult(true, $"Restored {restored} file(s) from {Path.GetFileName(zipPath)}. Your previous saves were backed up first{(safety.ZipPath is { } z ? $" ({Path.GetFileName(z)})" : "")}.", zipPath);
    }

    private static void Rotate(string folder, int keep)
    {
        foreach (var old in Directory.EnumerateFiles(folder, "*.zip").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(Math.Max(1, keep)))
        {
            try { File.Delete(old); } catch (IOException) { }
        }
    }
}
