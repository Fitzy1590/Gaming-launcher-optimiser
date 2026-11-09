using System.Diagnostics;
using System.Net.Http;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Mods;
using FitzLauncher.Core.Scanning;
using FitzLauncher.Core.Trainers;

namespace FitzLauncher.App.Services.Mods;

/// <summary>
/// Per-game mod management: plan → extract to the launcher's staging folder → copy into the game (originals
/// backed up), turn on/off, reorder, remove, plus the compatibility check, troubleshooter and error report.
/// Staging lives in %LOCALAPPDATA%\FitzLauncher\mods\{game}.
/// </summary>
public sealed class ModService(AppServices s)
{
    private readonly Lock _lock = new();

    public string GameRoot(Game g) => GameScanner.ResolveInstallRoot(g);
    private string Root(Game g) => Path.Combine(s.Paths.Root, "mods", g.Id.ToString("N"));
    public ModDeployer Deployer(Game g) => new(GameRoot(g), Path.Combine(Root(g), "staged"), Path.Combine(Root(g), "originals"));
    public GameModLayout Layout(Game g) => GameModLayout.Detect(GameRoot(g), g.ExePath);
    public string DownloadsDir => Path.Combine(s.Paths.Root, "downloads");

    /// <summary>Exe version, else the repack's version from its folder name.</summary>
    public string? GameVersion(Game g)
    {
        var exe = g.LastScan?.RenderExePath ?? g.ExePath;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exe);
            var v = vi.ProductVersion ?? vi.FileVersion;
            if (!string.IsNullOrWhiteSpace(v) && v.Any(char.IsDigit) && v != "1.0.0.0") return v.Split(' ')[0];
        }
        catch (FileNotFoundException) { }
        return FlingParser.VersionFromName(Path.GetFileName(GameRoot(g))) ?? FlingParser.VersionFromName(g.Name);
    }

    private static string? PluginsTxtPath(GameModLayout layout)
    {
        if (layout.PluginsFolderName is not { } folder) return null;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var gog = Path.Combine(local, folder + " GOG");
        return Path.Combine(Directory.Exists(gog) ? gog : Path.Combine(local, folder), "Plugins.txt");
    }

    // ------------------------------------------------------------------ install

    /// <summary>The FOMOD installer's option groups, or null when the archive has none (or only fixed choices).</summary>
    public List<FomodGroup>? FomodGroups(string source)
    {
        var entry = ModArchive.ListEntries(source).FirstOrDefault(e => e.EndsWith("fomod/ModuleConfig.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null || ModArchive.ReadText(source, entry) is not { } xml) return null;
        var groups = FomodInstaller.Describe(xml).Where(gr => gr.Type != "SelectAll" && gr.Options.Count > 1).ToList();
        return groups.Count == 0 ? null : groups;
    }

    public ModInstallPlan Plan(Game g, string source, string modName, string? overrideRoot = null, IReadOnlyDictionary<string, HashSet<string>>? fomodChoices = null)
    {
        var root = GameRoot(g);
        var layout = Layout(g);
        var entries = ModArchive.ListEntries(source);
        var fomodEntry = entries.FirstOrDefault(e => e.EndsWith("fomod/ModuleConfig.xml", StringComparison.OrdinalIgnoreCase));
        var fomod = fomodEntry is null ? null : ModArchive.ReadText(source, fomodEntry);
        return ModInstallPlanner.Plan(entries, layout, modName, rel => File.Exists(Path.Combine(root, rel)) || Directory.Exists(Path.Combine(root, rel)), fomod, overrideRoot, fomodChoices);
    }

    /// <summary>Installs (or updates, when the same store mod is already installed) and turns it on.</summary>
    public InstalledMod Install(Game g, string source, ModInstallPlan plan, InstalledMod meta)
    {
        lock (_lock)
        {
            var mods = g.ModSetup.Mods;
            var deployer = Deployer(g);
            var existing = meta.SourceId is null ? null : mods.FirstOrDefault(m => m.Source == meta.Source && m.SourceId == meta.SourceId);
            if (existing is not null)
            {
                deployer.Undeploy(existing, mods);
                RemovePlugins(g, existing);
                mods.Remove(existing);
                TryDelete(deployer.StagingFor(existing));
                meta.Priority = existing.Priority;
            }
            else meta.Priority = mods.Count == 0 ? 0 : mods.Max(m => m.Priority) + 1;

            var staging = deployer.StagingFor(meta);
            TryDelete(staging);
            meta.Files = ModArchive.Extract(source, plan.Files, staging).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            meta.Requires = plan.Requires;
            meta.GameVersionAtInstall = GameVersion(g);
            meta.InstallNotes = string.Join(" ", new[] { $"Installed to: {plan.TargetLabel}." }.Concat(plan.Notes));
            meta.Enabled = true;
            mods.Add(meta);
            var errors = deployer.Deploy(meta, mods);
            if (PluginsTxtPath(Layout(g)) is { } p && plan.Plugins.Count > 0) PluginsTxt.Add(p, plan.Plugins, Layout(g).PluginsStarFormat);
            if (errors.Count > 0) s.Log.Write($"[{g.Name}] mod {meta.Name}: {string.Join("; ", errors)}");
            s.SaveLibrary();
            return meta;
        }
    }

    // ---- guided Nexus installs for free accounts: one files page at a time, requirements first
    private readonly Dictionary<Guid, List<(long Id, string Name)>> _nexusQueue = [];

    public void QueueNexus(Game g, IEnumerable<(long Id, string Name)> mods)
    {
        lock (_nexusQueue) _nexusQueue[g.Id] = [.. mods];
    }

    /// <summary>Marks a Nexus mod as installed; returns the next one to open and how many are left, or null when done / not queued.</summary>
    public ((long Id, string Name) Next, int Left)? AdvanceNexus(Game g, long installedId, out bool finished)
    {
        finished = false;
        lock (_nexusQueue)
        {
            if (!_nexusQueue.TryGetValue(g.Id, out var q) || q.RemoveAll(x => x.Id == installedId) == 0) return null;
            if (q.Count == 0) { _nexusQueue.Remove(g.Id); finished = true; return null; }
            return (q[0], q.Count);
        }
    }

    public void SetEnabled(Game g, InstalledMod mod, bool on)
    {
        lock (_lock)
        {
            if (mod.Enabled == on) return;
            var d = Deployer(g);
            var layout = Layout(g);
            if (on)
            {
                mod.Enabled = true;
                d.Deploy(mod, g.ModSetup.Mods);
                if (PluginsTxtPath(layout) is { } p) PluginsTxt.Add(p, Plugins(mod, layout), layout.PluginsStarFormat);
            }
            else
            {
                d.Undeploy(mod, g.ModSetup.Mods);
                mod.Enabled = false;
                RemovePlugins(g, mod);
            }
            s.SaveLibrary();
        }
    }

    /// <summary>Moves a mod later (+1, wins more conflicts) or earlier (-1) in the order.</summary>
    public void Move(Game g, InstalledMod mod, int direction)
    {
        lock (_lock)
        {
            var ordered = g.ModSetup.Mods.OrderBy(m => m.Priority).ToList();
            var i = ordered.IndexOf(mod);
            var j = i + direction;
            if (i < 0 || j < 0 || j >= ordered.Count) return;
            var other = ordered[j];
            var d = Deployer(g);
            var all = g.ModSetup.Mods;
            var overlap = mod.Enabled && other.Enabled && mod.Files.Intersect(other.Files, StringComparer.OrdinalIgnoreCase).Any();
            if (overlap)
            {
                var (hi, lo) = mod.Priority > other.Priority ? (mod, other) : (other, mod);
                d.Undeploy(hi, all); hi.Enabled = false;
                d.Undeploy(lo, all); lo.Enabled = false;
                (mod.Priority, other.Priority) = (other.Priority, mod.Priority);
                var (first, second) = mod.Priority < other.Priority ? (mod, other) : (other, mod);
                first.Enabled = true; d.Deploy(first, all);
                second.Enabled = true; d.Deploy(second, all);
            }
            else (mod.Priority, other.Priority) = (other.Priority, mod.Priority);
            s.SaveLibrary();
        }
    }

    public void Remove(Game g, InstalledMod mod)
    {
        lock (_lock)
        {
            var d = Deployer(g);
            d.Undeploy(mod, g.ModSetup.Mods);
            RemovePlugins(g, mod);
            g.ModSetup.Mods.Remove(mod);
            TryDelete(d.StagingFor(mod));
            s.SaveLibrary();
        }
    }

    /// <summary>Takes every mod out, putting the game back to its original files.</summary>
    public void RemoveAll(Game g)
    {
        foreach (var m in g.ModSetup.Mods.OrderByDescending(m => m.Priority).ToList()) Remove(g, m);
    }

    /// <summary>Turns mods on/off to match a set (used by Find the bad mod).</summary>
    public void ApplyEnableSet(Game g, HashSet<Guid> enabled)
    {
        foreach (var m in g.ModSetup.Mods.OrderByDescending(m => m.Priority).Where(m => m.Enabled && !enabled.Contains(m.Id)).ToList()) SetEnabled(g, m, false);
        foreach (var m in g.ModSetup.Mods.OrderBy(m => m.Priority).Where(m => !m.Enabled && enabled.Contains(m.Id)).ToList()) SetEnabled(g, m, true);
    }

    private static IEnumerable<string> Plugins(InstalledMod m, GameModLayout layout) =>
        layout.DataDir is not { } data ? []
            : m.Files.Where(f => (f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                                  && string.Equals(Path.GetDirectoryName(f)?.Replace('\\', '/'), data, StringComparison.OrdinalIgnoreCase)).Select(f => Path.GetFileName(f));

    private void RemovePlugins(Game g, InstalledMod m)
    {
        var layout = Layout(g);
        if (PluginsTxtPath(layout) is { } p) PluginsTxt.Remove(p, Plugins(m, layout));
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ check & troubleshoot

    public List<ModIssue> Check(Game g)
    {
        var root = GameRoot(g);
        var d = Deployer(g);
        var tweakFiles = g.AppliedChanges.Select(c => c.Action).OfType<FileReplaceAction>()
            .Select(a => GameModLayout.Rel(root, a.TargetPath)).ToList();
        var exe = g.LastScan?.RenderExePath ?? g.ExePath;
        return ModCompatibility.Check(new ModCheckContext
        {
            Game = g, Layout = Layout(g), GameRoot = root, GameVersion = GameVersion(g), GameIs64Bit = PeImports.Is64Bit(exe),
            StagedPath = (m, rel) => Path.Combine(d.StagingFor(m), rel.Replace('/', Path.DirectorySeparatorChar)), TweakFiles = tweakFiles,
        });
    }

    /// <summary>
    /// Before a game starts: checks the mod setup, repairs what can be repaired without asking (turns required mods
    /// back on, puts back mod files a game update or file check removed), and returns the problems left (errors only).
    /// </summary>
    public (int Fixed, List<ModIssue> Remaining) PreLaunchCheck(Game g)
    {
        var fixedCount = 0;
        foreach (var issue in Check(g).Where(i => i.AutoFix != AutoFix.None))
        {
            if (g.ModSetup.Mods.FirstOrDefault(m => m.Id == issue.FixModId) is not { } mod) continue;
            try
            {
                if (issue.AutoFix == AutoFix.EnableMod && !mod.Enabled) { SetEnabled(g, mod, true); fixedCount++; }
                else if (issue.AutoFix == AutoFix.Redeploy)
                {
                    lock (_lock) { if (Deployer(g).Deploy(mod, g.ModSetup.Mods).Count == 0) fixedCount++; }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s.Log.Write($"[{g.Name}] auto-fix {issue.Title}: {e.Message}"); }
        }
        if (fixedCount > 0) s.SaveLibrary();
        return (fixedCount, Check(g).Where(i => i.Severity == IssueSeverity.Error).ToList());
    }

    public List<LogFile> Logs(Game g)
    {
        var since = (g.LastCrash?.When ?? g.LastPlayed ?? DateTimeOffset.Now.AddDays(-1)).UtcDateTime.AddHours(-3);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var localLow = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!, "LocalLow");
        return ModLogLocator.Find(GameRoot(g), Layout(g), since, docs, localLow);
    }

    public TroubleReport Troubleshoot(Game g, List<ModIssue> compat, List<LogFile> logs) => ModTroubleshooter.Analyze(g, Layout(g), logs, compat);

    public string Report(Game g, List<ModIssue> compat, TroubleReport trouble, List<LogFile> logs)
    {
        var hw = s.Hardware is { } h ? $"{h.CpuName} · {h.PrimaryGpu?.Name} · {h.RamGb:0} GB · {h.OsName} {h.OsBuild}" : "not scanned";
        return ModErrorReport.Build(g, Layout(g), GameVersion(g), hw, compat, trouble, logs);
    }

    // ------------------------------------------------------------------ mod updates

    /// <summary>Asks each mod's store for its newest version. Returns how many have an update.</summary>
    public async Task<int> CheckUpdatesAsync(Game g, CancellationToken ct = default)
    {
        var setup = g.ModSetup;
        var count = 0;
        foreach (var m in setup.Mods.Where(m => m.SourceId is not null))
        {
            string? latest = null;
            try
            {
                switch (m.Source)
                {
                    case ModSource.Nexus when s.Nexus is { } nx && setup.NexusDomain is { } domain && long.TryParse(m.SourceId, out var id):
                        latest = (await nx.ModAsync(domain, id, ct)).Version;
                        break;
                    case ModSource.Thunderstore when setup.ThunderstoreCommunity is { } community:
                        latest = (await s.Thunderstore.PackagesAsync(community, ct)).FirstOrDefault(p => p.Id.Equals(m.SourceId, StringComparison.OrdinalIgnoreCase))?.Version;
                        break;
                    case ModSource.ModIo when s.ModIo is { } io && setup.ModIoGameId is { } gid && long.TryParse(m.SourceId, out var mid):
                        latest = (await io.ModAsync(gid, mid, ct))?.Version;
                        break;
                }
            }
            catch (Exception e) when (e is StoreException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { continue; }
            m.UpdateCheckedAt = DateTimeOffset.Now;
            m.UpdateVersion = IsNewer(latest, m.Version) ? latest : null;
            if (m.UpdateVersion is not null) count++;
        }
        s.SaveLibrary();
        return count;
    }

    private static bool IsNewer(string? latest, string? installed)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (FitzLauncher.Core.GameUpdates.GameUpdateLogic.Compare(latest, installed) is { } c) return c > 0;
        return installed is not null && !latest.Trim().Equals(installed.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ downloads

    public async Task<string> DownloadAsync(HttpClient http, string url, string fileName, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(DownloadsDir);
        var path = Path.Combine(DownloadsDir, ModInstallPlanner.SafeName(Path.GetFileName(fileName)));
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using (var dst = File.Create(path))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }
        return path;
    }
}
