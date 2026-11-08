using FitzLauncher.Core.Mods;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Core.GameUpdates;

public enum UpdatePackageKind { Files, Installer, InstallerInArchive, Unknown }

/// <summary>What an update file is and, for an archive of game files, where each file goes.</summary>
public sealed class UpdatePlan
{
    public UpdatePackageKind Kind { get; set; }
    public List<PlannedFile> Files { get; set; } = [];
    /// <summary>How many files in the update replace files the game already has (the confidence signal).</summary>
    public int MatchedExisting { get; set; }
    public string? InstallerEntry { get; set; }
    public string Summary { get; set; } = "";
    public bool Confident => Kind == UpdatePackageKind.Installer || (Kind == UpdatePackageKind.Files && MatchedExisting > 0) || Kind == UpdatePackageKind.InstallerInArchive;
}

/// <summary>Files an update overwrote and created, so it can be undone exactly.</summary>
public sealed class UpdateManifest
{
    public List<string> Overwritten { get; set; } = [];
    public List<string> Created { get; set; } = [];
    /// <summary>Full copy of the game folder (installer updates, whose changes can't be known in advance).</summary>
    public List<string> FullCopy { get; set; } = [];
}

/// <summary>
/// Applies a game update the user already has, safely: plan → back up exactly what will change (or the whole
/// game for installers) → apply → verify; undo restores overwritten files and removes new ones.
/// </summary>
public sealed class GameUpdater(string gameRoot, string backupDir)
{
    private string FilesDir => Path.Combine(backupDir, "files");
    private string ManifestPath => Path.Combine(backupDir, "manifest.json");

    public static UpdatePlan Plan(string source, IReadOnlyList<string> entries, Func<string, bool> existsInGame)
    {
        if (!Directory.Exists(source) && Path.GetExtension(source).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new UpdatePlan { Kind = UpdatePackageKind.Installer, Summary = "Update installer: it will be run, with a full backup first." };

        var files = entries.Select(ModInstallPlanner.Normalize).Where(e => e.Length > 0 && !e.EndsWith('/')).ToList();
        if (files.Count == 0) return new UpdatePlan { Kind = UpdatePackageKind.Unknown, Summary = "The update is empty." };

        // Try stripping 0..3 wrapper folders; keep the mapping that hits the most existing game files.
        var best = (prefix: "", hits: -1);
        var prefix = "";
        for (var depth = 0; depth <= 3; depth++)
        {
            var hits = files.Count(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && existsInGame(f[prefix.Length..]));
            if (hits > best.hits) best = (prefix, hits);
            var tops = files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(f => f[prefix.Length..].Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tops.Count != 1 || files.Any(f => f.Length > prefix.Length && !f[prefix.Length..].Contains('/'))) break;
            prefix += tops[0] + "/";
        }

        if (best.hits <= 0)
        {
            // An installer packed in an archive (common for patches): run it after unpacking.
            var exe = files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("update", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("patch", StringComparison.OrdinalIgnoreCase))
                .ThenBy(f => f.Count(c => c == '/')).FirstOrDefault();
            if (exe is not null && files.Count <= 40)
                return new UpdatePlan { Kind = UpdatePackageKind.InstallerInArchive, InstallerEntry = exe, Summary = $"Contains an installer ({Path.GetFileName(exe)}): it will be unpacked and run, with a full backup first." };
            return new UpdatePlan
            {
                Kind = UpdatePackageKind.Unknown,
                Files = files.Select(f => new PlannedFile(f, f)).ToList(),
                Summary = "None of the files in this update match files in the game folder, so it's not clear where they go.",
            };
        }

        var planned = files.Where(f => f.StartsWith(best.prefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => new PlannedFile(f, f[best.prefix.Length..])).Where(p => ModInstallPlanner.IsSafeRelative(p.Dest)).ToList();
        return new UpdatePlan
        {
            Kind = UpdatePackageKind.Files, Files = planned, MatchedExisting = best.hits,
            Summary = $"{planned.Count} file(s): {best.hits} replace existing game files, {planned.Count - best.hits} are new.",
        };
    }

    /// <summary>Backs up the game files this plan will replace and records which files it creates.</summary>
    public UpdateManifest BackupFor(UpdatePlan plan)
    {
        var m = new UpdateManifest();
        foreach (var f in plan.Files)
        {
            var target = Game(f.Dest);
            if (File.Exists(target))
            {
                var copy = Path.Combine(FilesDir, f.Dest.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(target, copy, true);
                m.Overwritten.Add(f.Dest);
            }
            else m.Created.Add(f.Dest);
        }
        Json.Save(ManifestPath, m);
        return m;
    }

    /// <summary>Copies the whole game folder (for installers). The caller checks free space first.</summary>
    public UpdateManifest FullBackup()
    {
        var m = new UpdateManifest();
        foreach (var file in Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories))
        {
            var rel = GameModLayout.Rel(gameRoot, file);
            var copy = Path.Combine(FilesDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, true);
            m.FullCopy.Add(rel);
        }
        Json.Save(ManifestPath, m);
        return m;
    }

    public static long FolderSize(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    public List<string> ApplyFiles(string source, UpdatePlan plan) => ModArchive.Extract(source, plan.Files, gameRoot);

    /// <summary>Planned files that are missing from the game after applying (empty = OK).</summary>
    public List<string> Verify(UpdatePlan plan) =>
        plan.Files.Where(f => !File.Exists(Game(f.Dest))).Select(f => f.Dest).ToList();

    /// <summary>
    /// Puts the game back as it was before the update. <paramref name="protect"/> marks game-relative paths
    /// that must never be deleted or overwritten (save folders inside the game directory).
    /// </summary>
    public List<string> Rollback(Func<string, bool>? protect = null)
    {
        protect ??= _ => false;
        var errors = new List<string>();
        var m = Json.Load<UpdateManifest>(ManifestPath) ?? throw new InvalidOperationException("The backup's record is missing.");
        if (m.FullCopy.Count > 0)
        {
            var keep = m.FullCopy.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories).ToList())
            {
                var rel = GameModLayout.Rel(gameRoot, file);
                if (!keep.Contains(rel) && !protect(rel)) Try(() => File.Delete(file), file, errors);
            }
            foreach (var rel in m.FullCopy.Where(r => !protect(r))) Restore(rel, errors);
            return errors;
        }
        foreach (var rel in m.Overwritten.Where(r => !protect(r))) Restore(rel, errors);
        foreach (var rel in m.Created.Where(r => !protect(r))) Try(() => { if (File.Exists(Game(rel))) File.Delete(Game(rel)); }, rel, errors);
        return errors;
    }

    private void Restore(string rel, List<string> errors)
    {
        var copy = Path.Combine(FilesDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Try(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Game(rel))!);
            File.Copy(copy, Game(rel), true);
        }, rel, errors);
    }

    private static void Try(Action a, string what, List<string> errors)
    {
        try { a(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"{what}: {e.Message}"); }
    }

    private string Game(string rel) => Path.Combine(gameRoot, rel.Replace('/', Path.DirectorySeparatorChar));
}
