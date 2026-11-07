using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Storage;

/// <summary>
/// Moves an installed game to another drive (e.g. SD card → NVMe): copies with progress, verifies
/// every file's size, re-points the library entry (exe, install folder, saves, edited config, swapped DLLs)
/// and only then removes the old copy. If anything fails, the original stays where it was.
/// </summary>
public static class GameMover
{
    public sealed record MoveResult(bool Ok, string Message, string? NewInstallDir = null);

    public static long FolderSize(string dir) =>
        Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length)
            : 0;

    /// <param name="destinationParent">Folder the game folder goes into, e.g. "C:\Games".</param>
    /// <param name="freeBytes">Free space on the destination drive (checked before copying).</param>
    public static MoveResult Move(Game game, string destinationParent, long freeBytes, bool deleteSource,
        IProgress<(long done, long total, string file)>? progress = null, CancellationToken ct = default)
    {
        var source = game.InstallDir.TrimEnd('\\', '/');
        if (!Directory.Exists(source)) return new MoveResult(false, "The game folder doesn't exist.");
        var target = Path.Combine(destinationParent, Path.GetFileName(source));
        if (Path.GetFullPath(target).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            return new MoveResult(false, "That's where the game already is.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return new MoveResult(false, $"{target} already exists and isn't empty.");

        var files = Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList();
        var total = files.Sum(f => new FileInfo(f).Length);
        if (total > freeBytes - 512L * 1024 * 1024) return new MoveResult(false, $"Not enough space: needs {total / 1e9:0.0} GB, {freeBytes / 1e9:0.0} GB free.");

        long done = 0;
        var buffer = new byte[4 * 1024 * 1024];
        try
        {
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(source, f);
                var dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, read);
                        done += read;
                        progress?.Report((done, total, rel));
                    }
                }
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(f));
            }

            // Verify before touching the original.
            foreach (var f in files)
            {
                var dest = Path.Combine(target, Path.GetRelativePath(source, f));
                if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(f).Length)
                    throw new IOException($"Verification failed for {Path.GetRelativePath(source, f)}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); } catch (IOException) { }
            return new MoveResult(false, e is OperationCanceledException ? "Move cancelled. The game is still in its original place." : "Move failed, game left where it was: " + e.Message);
        }

        Repoint(game, source, target);
        if (deleteSource)
        {
            try { Directory.Delete(source, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new MoveResult(true, $"Moved to {target}, but the old copy couldn't be removed ({e.Message}). Delete it manually.", target);
            }
        }
        return new MoveResult(true, $"Moved to {target} ({total / 1e9:0.0} GB).", target);
    }

    /// <summary>Re-points every stored path inside the old install folder to the new one.</summary>
    public static void Repoint(Game game, string oldRoot, string newRoot)
    {
        string Map(string p) => p.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase) ? newRoot + p[oldRoot.Length..] : p;
        game.ExePath = Map(game.ExePath);
        game.InstallDir = Map(game.InstallDir);
        if (game.CustomConfigDir is { } c) game.CustomConfigDir = Map(c);
        game.SaveDirs = game.SaveDirs.Select(Map).ToList();
        foreach (var change in game.AppliedChanges)
        {
            switch (change.Action)
            {
                case IniSetAction i: i.FilePath = Map(i.FilePath); break;
                case KeyValueFileSetAction k: k.FilePath = Map(k.FilePath); break;
                case FileReplaceAction f: f.TargetPath = Map(f.TargetPath); break;
            }
        }
        game.LastScan = null; // paths inside changed: rescan on next view
    }
}
