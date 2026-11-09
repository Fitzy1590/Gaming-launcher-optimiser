using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services.Mods;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Mods;

namespace FitzLauncher.App.ViewModels;

/// <summary>Mod marketplace: rankings and paging, compatibility, install with requirements, your Nexus account.</summary>
public sealed partial class ModsViewModel
{
    public static IReadOnlyList<KeyValuePair<ModRanking, string>> Rankings { get; } =
        [.. Enum.GetValues<ModRanking>().Select(r => new KeyValuePair<ModRanking, string>(r, NexusQuery.Label(r)))];

    [ObservableProperty] private ModRanking _ranking = ModRanking.MostDownloaded;
    [ObservableProperty] private bool _canLoadMore;
    private int _offset;
    private int _total;
    private readonly Dictionary<long, RequirementPlan> _plans = [];

    [RelayCommand] private void SetRanking(ModRanking r) { if (Ranking == r) SearchCommand.Execute(null); else Ranking = r; }

    partial void OnRankingChanged(ModRanking value)
    {
        if (Source == ModSource.Nexus && !IsBusy) SearchCommand.Execute(null);
    }

    private NexusClient Nexus => _s.Nexus ?? throw new StoreException("Add your Nexus API key in Settings → Mods first (free account).");
    private string Domain => Game.ModSetup.NexusDomain ?? throw new StoreException("This game isn't on Nexus Mods (or wasn't matched). Use Link stores.");

    /// <summary>Nexus page of results for the current ranking (first page, or the next one).</summary>
    private async Task<List<ModSearchResult>> NexusPageAsync(bool more)
    {
        if (!more) _offset = 0;
        var (results, total) = await Nexus.SearchAsync(Domain, Query, Ranking, _offset);
        _offset += results.Count;
        _total = total;
        CanLoadMore = _offset < _total && results.Count > 0;
        return results;
    }

    [RelayCommand]
    private Task LoadMoreAsync() => Busy("Loading more…", async () =>
    {
        var page = await NexusPageAsync(more: true);
        var rows = page.Select(r => new ModResultRow(r, this) { IsInstalled = IsInstalled(r) }).ToList();
        foreach (var row in rows) Results.Add(row);
        Status = $"{Results.Count} of {_total} · {NexusQuery.Label(Ranking)}";
        foreach (var row in rows) _ = LoadImageAsync(row);
    });

    private bool InstalledNexus(long id) => Game.ModSetup.Mods.Any(m => m.Source == ModSource.Nexus && m.SourceId == id.ToString());

    private async Task<RequirementPlan> PlanAsync(long modId, string name)
    {
        if (_plans.TryGetValue(modId, out var cached)) return cached;
        var gameId = await Nexus.GameIdAsync(Domain) ?? throw new StoreException("Couldn't find this game's Nexus id.");
        var plan = await RequirementResolver.ResolveAsync(modId, name, id => Nexus.RequirementsAsync(gameId, id), InstalledNexus);
        return _plans[modId] = plan;
    }

    /// <summary>Compatibility with this install: requirements (recursively), external tools, DLC, anti-cheat, game updates.</summary>
    public Task CheckCompatAsync(ModResultRow row) => Busy($"Checking {row.Name}…", async () =>
    {
        RequirementPlan? plan = row.IsNexus ? await PlanAsync(long.Parse(row.Result.Id), row.Name) : null;
        var gameUpdated = File.Exists(Game.ExePath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(Game.LastScan?.RenderExePath ?? Game.ExePath)) : (DateTimeOffset?)null;
        var report = ModPreInstallCheck.Assess(_s.Mods.Layout(Game), plan, row.Result.Updated, gameUpdated, deprecated: row.Result.Deprecated);
        row.Verdict = report.Verdict;
        row.CompatText = report.Summary;
        row.CompatDetail = string.Join(" ", report.Reasons) + (plan?.Notes.Count > 0 ? " " + string.Join(" ", plan.Notes) : "");
        Status = $"{row.Name}: {report.Summary.ToLowerInvariant()}.";
    });

    /// <summary>
    /// One-click install: the mod and every Nexus mod it requires (requirements first, main files). Premium accounts
    /// download directly; free accounts get each files page opened in turn and "Mod Manager Download" finishes it.
    /// </summary>
    public Task InstallWithRequirementsAsync(ModResultRow row) => Busy($"Planning {row.Name}…", async () =>
    {
        var plan = await PlanAsync(long.Parse(row.Result.Id), row.Name);
        var toInstall = plan.Install.Where(x => !InstalledNexus(x.ModId) || x.ModId.ToString() == row.Result.Id).ToList();
        var extras = new List<string>();
        if (plan.External.Count > 0) extras.Add("Also needs (from outside Nexus): " + string.Join(", ", plan.External.Select(e => e.Name)) + ".");
        if (plan.Dlc.Count > 0) extras.Add("Needs DLC: " + string.Join(", ", plan.Dlc) + ".");
        if (toInstall.Count > 1 || extras.Count > 0)
        {
            var text = $"Install {row.Name}" + (toInstall.Count > 1 ? $" with {toInstall.Count - 1} required mod(s): {string.Join(", ", toInstall.Take(toInstall.Count - 1).Select(x => x.Name))}?" : "?")
                + (extras.Count > 0 ? "\n\n" + string.Join("\n", extras) : "");
            if (MessageBox.Show(text, "Fitz Launcher · install with requirements", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { Status = "Cancelled."; return; }
        }

        if (!(await Nexus.ValidateAsync()).IsPremium)
        {
            // Free account: Nexus needs a click per file, so walk through them one page at a time.
            if (!_s.Settings.NxmHandler)
                throw new StoreException("Turn on \"Open Nexus Mod Manager Download links in Fitz Launcher\" in Settings → Mods first, so Fitz can pick up each download.");
            _s.Mods.QueueNexus(Game, toInstall);
            Open(NexusClient.FilesUrl(Domain, toInstall[0].ModId.ToString()));
            foreach (var e in plan.External.Where(e => e.Url is not null)) Open(e.Url!);
            Status = toInstall.Count == 1
                ? $"Opened {toInstall[0].Name} on Nexus: click \"Mod Manager Download\" (Slow download is fine) and Fitz installs it."
                : $"Step 1 of {toInstall.Count}: opened {toInstall[0].Name} on Nexus. Click \"Mod Manager Download\"; when it's installed Fitz opens the next one.";
            return;
        }
        var done = 0;
        foreach (var (id, name) in toInstall)
        {
            var file = await Nexus.PrimaryFileAsync(Domain, id) ?? throw new StoreException($"{name} has no downloadable file.");
            Status = $"Downloading {name} ({++done}/{toInstall.Count})…";
            var url = await Nexus.DownloadUrlAsync(Domain, id, long.Parse(file.FileId));
            var path = await _s.Mods.DownloadAsync(_s.StoreHttp, url, file.FileName ?? $"{id}-{file.FileId}.zip",
                new Progress<double>(p => Status = $"Downloading {name} ({done}/{toInstall.Count})… {p:P0}"), CancellationToken.None);
            await InstallFileAsync(path, new InstalledMod
            {
                Source = ModSource.Nexus, SourceId = id.ToString(), FileId = file.FileId, Name = name, Version = file.Version,
                PageUrl = NexusClient.PageUrl(Domain, id.ToString()),
            }, confirm: id.ToString() == row.Result.Id); // FOMOD choices for the mod you picked; defaults for requirements
        }
        foreach (var e in plan.External.Where(e => e.Url is not null)) Open(e.Url!);
        _plans.Clear();
        row.IsInstalled = true;
        Status = $"Installed {row.Name}" + (toInstall.Count > 1 ? $" and {toInstall.Count - 1} requirement(s)" : "") + "." + (plan.External.Count > 0 ? " Opened the pages for tools from outside Nexus." : "");
    });

    /// <summary>Free-account queue: after each install, open the next mod's Nexus page.</summary>
    private void AdvanceNexusQueue(InstalledMod mod)
    {
        if (!long.TryParse(mod.SourceId, out var id) || Game.ModSetup.NexusDomain is not { } domain) return;
        if (_s.Mods.AdvanceNexus(Game, id, out var finished) is { } step)
        {
            Open(NexusClient.FilesUrl(domain, step.Next.Id.ToString()));
            Status += $" Next: {step.Next.Name} ({step.Left} left) is open on Nexus: click \"Mod Manager Download\".";
        }
        else if (finished) Status += " That was the last one: everything it needs is installed.";
    }

    /// <summary>After any Nexus install: remember its requirements (for the launch check) and track it on Nexus.</summary>
    private async Task AfterNexusInstallAsync(InstalledMod mod)
    {
        if (_s.Nexus is not { } nx || Game.ModSetup.NexusDomain is not { } domain || !long.TryParse(mod.SourceId, out var id)) return;
        try
        {
            if (await nx.GameIdAsync(domain) is { } gameId && await nx.RequirementsAsync(gameId, id) is { } info)
            {
                mod.NexusRequirements = [.. info.Mods.Where(r => !r.External && r.ModId is not null).Select(r => new ModRef(r.ModId!.Value, r.Name))];
                _s.SaveLibrary();
            }
            if (_s.Settings.NexusTrackOnInstall) await nx.TrackAsync(domain, id);
        }
        catch (Exception e) when (e is StoreException or System.Net.Http.HttpRequestException or TaskCanceledException) { _s.Log.Write($"Nexus after-install for {mod.Name}: {e.Message}"); }
    }

    // ---- your Nexus account

    [RelayCommand]
    private Task ShowTrackedAsync() => Busy("Reading your tracked mods…", async () =>
    {
        var tracked = (await Nexus.TrackedAsync()).Where(t => t.Domain.Equals(Domain, StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
        Results.Clear();
        CanLoadMore = false;
        foreach (var (_, id) in tracked)
        {
            try
            {
                var r = await Nexus.ModAsync(Domain, id);
                var row = new ModResultRow(r, this) { IsInstalled = IsInstalled(r) };
                Results.Add(row);
                _ = LoadImageAsync(row);
            }
            catch (StoreException) { }
        }
        var missing = Results.Count(r => !r.IsInstalled);
        Status = tracked.Count == 0 ? "You're not tracking any mods for this game on Nexus." : $"{tracked.Count} tracked on Nexus · {missing} not installed here.";
    });

    [RelayCommand]
    private Task TrackInstalledAsync() => Busy("Tracking your installed mods on Nexus…", async () =>
    {
        var n = 0;
        foreach (var m in Game.ModSetup.Mods.Where(m => m.Source == ModSource.Nexus && long.TryParse(m.SourceId, out _)))
        {
            await Nexus.TrackAsync(Domain, long.Parse(m.SourceId!));
            n++;
        }
        Status = n == 0 ? "No Nexus mods installed yet." : $"Tracking {n} mod(s) on your Nexus account: you'll see their updates there too.";
    });

    public Task EndorseAsync(ModRow row) => Busy($"Endorsing {row.Name}…", async () =>
    {
        await Nexus.EndorseAsync(Domain, long.Parse(row.Mod.SourceId!), row.Mod.Version);
        Status = $"Endorsed {row.Name} on Nexus. Thanks to the author!";
    });
}
