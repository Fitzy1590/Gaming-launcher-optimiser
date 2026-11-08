using System.IO.Compression;

namespace FitzLauncher.Core.Storage;

public sealed record SnapshotInfo(string Path, DateTimeOffset Taken, long Bytes)
{
    public string Label => $"{Taken.LocalDateTime:ddd d MMM yyyy, HH:mm} · {Bytes / 1024d:0} KB";
}

/// <summary>
/// Daily zip snapshots of the launcher's own data (library with mods/trainer/controller setups, settings,
/// session history, benchmarks, presets), so any day can be restored. Restoring snapshots the current state
/// first, so a restore can itself be undone.
/// </summary>
public static class LibrarySnapshots
{
    private static string Dir(AppPaths p) => Path.Combine(p.Root, "snapshots");
    private static IEnumerable<string> Files(AppPaths p) => [p.Library, p.Settings, p.Sessions, p.Benchmarks, p.Presets];

    public static List<SnapshotInfo> List(AppPaths p) =>
        Directory.Exists(Dir(p))
            ? Directory.EnumerateFiles(Dir(p), "snapshot-*.zip").Select(f => new FileInfo(f))
                .Select(f => new SnapshotInfo(f.FullName, new DateTimeOffset(f.CreationTimeUtc, TimeSpan.Zero), f.Length))
                .OrderByDescending(s => s.Taken).ToList()
            : [];

    /// <summary>Takes today's snapshot if there isn't one yet. Returns it, or null when not needed.</summary>
    public static SnapshotInfo? TakeIfDue(AppPaths p, int keep = 14, DateTime? today = null)
    {
        var day = (today ?? DateTime.Now).Date;
        if (List(p).Any(s => s.Taken.LocalDateTime.Date == day)) return null;
        return Take(p, keep);
    }

    public static SnapshotInfo? Take(AppPaths p, int keep = 14, string label = "")
    {
        var existing = Files(p).Where(File.Exists).ToList();
        if (existing.Count == 0) return null;
        Directory.CreateDirectory(Dir(p));
        var name = Path.Combine(Dir(p), $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}{label}.zip");
        using (var zip = ZipFile.Open(name, ZipArchiveMode.Create))
            foreach (var f in existing)
            {
                // Copy first: the launcher may be writing the live file.
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry(Path.GetFileName(f), CompressionLevel.Optimal).Open();
                src.CopyTo(dst);
            }
        foreach (var old in List(p).Skip(keep)) TryDelete(old.Path);
        var fi = new FileInfo(name);
        return new SnapshotInfo(name, DateTimeOffset.Now, fi.Length);
    }

    /// <summary>Puts a snapshot's files back (after snapshotting the current state). The launcher restarts afterwards.</summary>
    public static void Restore(AppPaths p, SnapshotInfo snapshot)
    {
        Take(p, keep: 30, label: "-before-restore");
        var allowed = Files(p).ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(snapshot.Path);
        foreach (var e in zip.Entries)
            if (allowed.TryGetValue(e.Name, out var target)) e.ExtractToFile(target, overwrite: true);
    }

    private static void TryDelete(string f)
    {
        try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
