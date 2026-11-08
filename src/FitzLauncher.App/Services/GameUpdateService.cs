using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using FitzLauncher.Core.GameUpdates;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Mods;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Trainers;

namespace FitzLauncher.App.Services;

public sealed record GuideLink(string Label, string Url);
/// <summary>Shown when an update can't be done automatically: what happened, the steps, and official links.</summary>
public sealed record UpdateGuide(string Title, string Reason, List<string> Steps, List<GuideLink> Links);
public sealed record UpdateResult(bool Success, string Message, UpdateGuide? Guide = null);

/// <summary>
/// Game updates from official sources only. Checks: Steam patch notes and public build date (for games
/// matched to a Steam app id) and GOG build versions. Installing: an update file you already have, with
/// saves and files backed up first, verified afterwards and rolled back automatically if anything fails.
/// </summary>
public sealed class GameUpdateService(AppServices s)
{
    private HttpClient Http => s.StoreHttp;

    // ------------------------------------------------------------------ versions

    /// <summary>Installed version: your override, else the repack folder's "v1.2.3", else the exe's version.</summary>
    public string? InstalledVersion(Game g)
    {
        if (g.Updates.InstalledVersionOverride is { Length: > 0 } o) return o;
        var root = GameScanner.ResolveInstallRoot(g);
        return FlingParser.VersionFromName(Path.GetFileName(root)) ?? FlingParser.VersionFromName(Path.GetFileName(Path.GetDirectoryName(g.ExePath) ?? ""))
               ?? FlingParser.VersionFromName(g.Name) ?? s.Mods.GameVersion(g);
    }

    private static DateTimeOffset? ExeDate(Game g)
    {
        var exe = g.LastScan?.RenderExePath ?? g.ExePath;
        return File.Exists(exe) ? new DateTimeOffset(File.GetLastWriteTimeUtc(exe), TimeSpan.Zero) : null;
    }

    public async Task<UpdateState> CheckAsync(Game g, CancellationToken ct = default)
    {
        var u = g.Updates;
        u.InstalledVersion = InstalledVersion(g);
        string? latest = null, notesTitle = null, notesUrl = null, store = null, source = null;
        DateTimeOffset? released = null;
        try
        {
            var appId = g.LastScan?.SteamAppId ?? g.SteamAppId;
            if (appId is { } id)
            {
                source = "Steam";
                store = $"https://store.steampowered.com/app/{id}";
                notesUrl = $"https://store.steampowered.com/news/app/{id}";
                var news = JsonNode.Parse(await Http.GetStringAsync($"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={id}&count=30&maxlength=1", ct));
                foreach (var item in news?["appnews"]?["newsitems"]?.AsArray() ?? [])
                {
                    var title = item?["title"]?.GetValue<string>() ?? "";
                    if (GameUpdateLogic.VersionFromTitle(title) is not { } v) continue;
                    latest = v;
                    notesTitle = title;
                    notesUrl = item?["url"]?.GetValue<string>() ?? notesUrl;
                    released = DateTimeOffset.FromUnixTimeSeconds(item?["date"]?.GetValue<long>() ?? 0);
                    break;
                }
                // Public branch build date (third-party mirror of Steam's app info); optional.
                try
                {
                    var info = JsonNode.Parse(await Http.GetStringAsync($"https://api.steamcmd.net/v1/info/{id}", ct));
                    if (info?["data"]?[id.ToString()]?["depots"]?["branches"]?["public"]?["timeupdated"]?.ToString() is { } ts && long.TryParse(ts, out var unix))
                    {
                        var built = DateTimeOffset.FromUnixTimeSeconds(unix);
                        if (released is null || built > released) released = built;
                    }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
            }
            if (latest is null)
            {
                var gog = u.GogProductId ?? await FindGogAsync(g.Name, ct);
                if (gog is { } pid)
                {
                    u.GogProductId = pid;
                    var builds = JsonNode.Parse(await Http.GetStringAsync($"https://content-system.gog.com/products/{pid}/os/windows/builds?generation=2", ct));
                    var first = (builds?["items"]?.AsArray() ?? []).FirstOrDefault(b => b?["public"]?.GetValue<bool>() != false && b?["version_name"] is not null);
                    if (first is not null)
                    {
                        source = "GOG";
                        latest = first["version_name"]!.GetValue<string>();
                        released = DateTimeOffset.TryParse(first["date_published"]?.ToString(), out var d) ? d : released;
                        store ??= $"https://www.gog.com/game/{pid}";
                        notesTitle = $"GOG build {latest}";
                    }
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            u.State = UpdateState.CantCheck;
            u.Detail = $"Couldn't reach the store: {e.Message}";
            u.CheckedAt = DateTimeOffset.Now;
            return u.State;
        }

        (u.State, u.Detail) = GameUpdateLogic.Decide(u.InstalledVersion, latest, released, ExeDate(g));
        if (source is null) u.Detail = "This game isn't matched to Steam or GOG, so there's no official version to compare with. Set its Steam app id in Edit to enable checks.";
        u.LatestVersion = latest;
        u.LatestReleased = released;
        u.Source = source;
        u.PatchNotesTitle = notesTitle;
        u.PatchNotesUrl = notesUrl;
        u.StoreUrl = store;
        u.CheckedAt = DateTimeOffset.Now;
        return u.State;
    }

    private async Task<long?> FindGogAsync(string name, CancellationToken ct)
    {
        static string K(string x) => new(x.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var clean = GameNameCleaner.Clean(name);
        var j = JsonNode.Parse(await Http.GetStringAsync($"https://embed.gog.com/games/ajax/filtered?mediaType=game&search={Uri.EscapeDataString(clean)}", ct));
        return (j?["products"]?.AsArray() ?? []).FirstOrDefault(p => K(p?["title"]?.GetValue<string>() ?? "") == K(clean))?["id"]?.GetValue<long>();
    }

    /// <summary>Checks every game; returns the ones with an update.</summary>
    public async Task<List<Game>> CheckAllAsync(IEnumerable<Game> games, CancellationToken ct = default)
    {
        var found = new List<Game>();
        foreach (var g in games.Where(g => !g.Hidden))
        {
            if (await CheckAsync(g, ct) is UpdateState.Available or UpdateState.ProbablyAvailable) found.Add(g);
            await Task.Delay(250, ct); // be gentle with the store APIs
        }
        s.Settings.LastGameUpdateCheck = DateTimeOffset.Now;
        s.SaveLibrary();
        s.SaveSettings();
        return found;
    }

    // ------------------------------------------------------------------ install

    private string BackupRoot(Game g) => Path.Combine(s.Paths.Root, "update-backups", g.Id.ToString("N"));

    public async Task<UpdateResult> InstallAsync(Game g, string source, IProgress<string> progress, bool gameRunning, CancellationToken ct = default)
    {
        if (gameRunning) return new(false, "Close the game before updating it.");
        var root = GameScanner.ResolveInstallRoot(g);
        if (!Directory.Exists(root)) return new(false, "The game folder wasn't found.", Guide(g, "The game folder is missing or moved. Fix the path with Edit first."));

        progress.Report("Reading the update…");
        UpdatePlan plan;
        try
        {
            var entries = Path.GetExtension(source).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? [] : ModArchive.ListEntries(source);
            plan = GameUpdater.Plan(source, entries, rel => File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or SharpCompress.Common.SharpCompressException or UnauthorizedAccessException)
        {
            return new(false, $"Couldn't read the update: {e.Message}", Guide(g, "The update file couldn't be opened (damaged, incomplete download, or password protected)."));
        }
        if (!plan.Confident) return new(false, plan.Summary, Guide(g, plan.Summary));

        // Saves first, always.
        progress.Report("Backing up saves…");
        await Task.Run(() => s.SaveBackups.Backup(g, s.BackupTargets(), s.Settings.Backups.KeepPerGame, force: true), ct);

        var from = InstalledVersion(g);
        var backupDir = Path.Combine(BackupRoot(g), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var updater = new GameUpdater(root, backupDir);
        string? tempDir = null;
        var record = new GameUpdateRecord { FromVersion = from, SourceFile = source, BackupDir = backupDir, FullBackup = plan.Kind != UpdatePackageKind.Files };

        try
        {
            if (plan.Kind == UpdatePackageKind.Files)
            {
                // Only the files the update replaces are copied.
                var need = plan.Files.Select(f => new FileInfo(Path.Combine(root, f.Dest.Replace('/', Path.DirectorySeparatorChar)))).Where(f => f.Exists).Sum(f => f.Length);
                if (!EnoughSpace(backupDir, need)) return new(false, "Not enough free space for a backup.", Guide(g, "There isn't enough free disk space to back up the files this update replaces."));
                progress.Report("Backing up the files the update replaces…");
                await Task.Run(() => updater.BackupFor(plan), ct);
                progress.Report("Applying the update…");
                await Task.Run(() => updater.ApplyFiles(source, plan), ct);
                var missing = updater.Verify(plan);
                if (missing.Count > 0) return await Fail($"{missing.Count} file(s) didn't install ({missing[0]}).");
            }
            else
            {
                var size = GameUpdater.FolderSize(root);
                if (!EnoughSpace(backupDir, (long)(size * 1.1)))
                    return new(false, $"A safe update needs a full backup of the game ({size / 1e9:0.#} GB) and there isn't enough free space.",
                        Guide(g, $"Installer updates can change any file, so the launcher copies the whole game first. Free up {size / 1e9:0.#} GB on {Path.GetPathRoot(s.Paths.Root)} or update manually."));
                progress.Report($"Backing up the whole game ({size / 1e9:0.#} GB)…");
                await Task.Run(() => updater.FullBackup(), ct);

                var installer = source;
                if (plan.Kind == UpdatePackageKind.InstallerInArchive)
                {
                    var temp = Path.Combine(Path.GetTempPath(), "FitzUpdate", Guid.NewGuid().ToString("N"));
                    var entries = ModArchive.ListEntries(source).Where(ModInstallPlanner.IsSafeRelative).Select(e => new PlannedFile(e, e)).ToList();
                    await Task.Run(() => ModArchive.Extract(source, entries, temp), ct);
                    installer = Path.Combine(temp, plan.InstallerEntry!.Replace('/', Path.DirectorySeparatorChar));
                    tempDir = temp;
                }
                progress.Report($"Running the installer: point it at {root}");
                using var p = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! })!;
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0) return await Fail($"The installer exited with code {p.ExitCode} (cancelled or failed).");
                if (!File.Exists(g.ExePath)) return await Fail("The game's exe is missing after the installer ran.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or SharpCompress.Common.SharpCompressException or System.ComponentModel.Win32Exception)
        {
            return await Fail(e.Message);
        }

        finally
        {
            if (tempDir is not null) try { Directory.Delete(tempDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        record.Success = true;
        record.ToVersion = FlingParser.VersionFromName(Path.GetFileName(source));
        if (record.ToVersion is not null) g.Updates.InstalledVersionOverride = record.ToVersion;
        record.Notes = plan.Summary;
        g.Updates.History.Insert(0, record);
        s.SaveLibrary();
        await CheckAsync(g, ct);
        s.SaveLibrary();
        return new(true, $"Updated {g.Name}{(record.ToVersion is { } v ? $" to {v}" : "")}. A backup of the previous version is kept in History.");

        async Task<UpdateResult> Fail(string why)
        {
            progress.Report("Something went wrong: restoring the previous version…");
            var errors = await Task.Run(() => updater.Rollback(InSaveFolder(g, root)));
            record.Success = false;
            record.RolledBack = errors.Count == 0;
            record.Notes = why;
            g.Updates.History.Insert(0, record);
            s.SaveLibrary();
            var restored = errors.Count == 0 ? "The game was restored to how it was before." : $"Restoring hit {errors.Count} problem(s); your backup is in {backupDir}.";
            return new(false, $"Update failed: {why} {restored}", Guide(g, why));
        }
    }

    /// <summary>True for game-relative paths inside one of the game's save folders.</summary>
    private static Func<string, bool> InSaveFolder(Game g, string root)
    {
        var saveRels = g.SaveDirs.Select(d => Path.GetFullPath(d)).Where(d => d.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            .Select(d => FitzLauncher.Core.Mods.GameModLayout.Rel(root, d).TrimEnd('/') + "/").ToList();
        return rel => saveRels.Any(sr => rel.StartsWith(sr, StringComparison.OrdinalIgnoreCase));
    }

    private static bool EnoughSpace(string dir, long bytes)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace > bytes + 2L * 1024 * 1024 * 1024; }
        catch (Exception e) when (e is ArgumentException or IOException) { return true; }
    }

    public async Task<UpdateResult> RestoreAsync(Game g, GameUpdateRecord r)
    {
        var root = GameScanner.ResolveInstallRoot(g);
        if (!Directory.Exists(r.BackupDir)) return new(false, "That backup no longer exists.");
        // Progress made since the update must survive: back up saves, and never touch save folders in the game dir.
        await Task.Run(() => s.SaveBackups.Backup(g, s.BackupTargets(), s.Settings.Backups.KeepPerGame, force: true));
        var errors = await Task.Run(() => new GameUpdater(root, r.BackupDir).Rollback(InSaveFolder(g, root)));
        r.RolledBack = errors.Count == 0;
        if (r.RolledBack && r.FromVersion is not null) g.Updates.InstalledVersionOverride = r.FromVersion;
        s.SaveLibrary();
        return errors.Count == 0 ? new(true, $"Restored {g.Name} to {r.FromVersion ?? "the previous version"}.") : new(false, $"Restore hit {errors.Count} problem(s): {errors[0]}");
    }

    /// <summary>Plain steps plus official links for updating by hand.</summary>
    public UpdateGuide Guide(Game g, string reason)
    {
        var root = GameScanner.ResolveInstallRoot(g);
        var u = g.Updates;
        var steps = new List<string>
        {
            "Close the game (and the trainer, if one is open).",
            "Your saves were backed up" + (u.History.FirstOrDefault()?.BackupDir is { } b ? $"; the previous version's files are in {b}." : ". Back up the game folder too if you have room."),
            $"Get the official update for your edition ({u.LatestVersion ?? "latest version"}) from the store or publisher you got the game from.",
            $"Run the update and point it at the game folder: {root}",
            "If it's a folder of files rather than an installer, copy them over the game folder, keeping the same folder layout.",
            "Back in the launcher, set the installed version on the game page (or press Check) so it knows you're up to date.",
        };
        var links = new List<GuideLink>();
        if (u.PatchNotesUrl is { } n) links.Add(new("Patch notes", n));
        if (u.StoreUrl is { } st) links.Add(new($"{u.Source ?? "Store"} page", st));
        if ((g.LastScan?.SteamAppId ?? g.SteamAppId) is { } id) links.Add(new("Steam support", $"https://help.steampowered.com/en/wizard/HelpWithGame/?appid={id}"));
        return new UpdateGuide($"Update {g.Name} by hand", reason, steps, links);
    }
}
