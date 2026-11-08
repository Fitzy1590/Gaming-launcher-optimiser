using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.App.Services.Mods;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Mods;
using Microsoft.Win32;

namespace FitzLauncher.App.ViewModels;

public sealed partial class ModRow(InstalledMod mod, ModsViewModel owner) : ObservableObject
{
    public InstalledMod Mod { get; } = mod;
    public string Name => Mod.Name;
    public string Meta => string.Join(" · ", new[] { Mod.Version, Mod.SourceLabel, Mod.Author is { Length: > 0 } a ? $"by {a}" : null, $"{Mod.Files.Count} files" }.Where(x => !string.IsNullOrEmpty(x)));
    public string Notes => string.Join(" ", new[] { Mod.Requires.Count > 0 ? $"Needs {string.Join(", ", Mod.Requires)}." : null, Mod.InstallNotes }.Where(x => x is not null));
    [ObservableProperty] private bool _hasIssue;

    public bool Enabled
    {
        get => Mod.Enabled;
        set { if (value != Mod.Enabled) _ = owner.ToggleAsync(this, value); }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
    [RelayCommand] private Task UpAsync() => owner.MoveAsync(this, -1);
    [RelayCommand] private Task DownAsync() => owner.MoveAsync(this, +1);
    [RelayCommand] private Task RemoveAsync() => owner.RemoveAsync(this);
    [RelayCommand] private void OpenPage() { if (Mod.PageUrl is { } u) ModsViewModel.Open(u); }
}

public sealed partial class ModResultRow(ModSearchResult r, ModsViewModel owner) : ObservableObject
{
    public ModSearchResult Result { get; } = r;
    public string Name => Result.Name;
    public string Summary => Result.Summary;
    public string Meta => string.Join(" · ", new[]
    {
        Result.Author is { Length: > 0 } a ? $"by {a}" : null, Result.Version is { Length: > 0 } v ? $"v{v}" : null,
        Result.Downloads > 0 ? $"{Fmt(Result.Downloads)} downloads" : null,
        Result.Endorsements > 0 ? $"{Fmt(Result.Endorsements)} {(Result.Source == ModSource.Nexus ? "endorsements" : "rating")}" : null,
        Result.Deprecated ? "deprecated" : null,
    }.Where(x => x is not null));
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private bool _isInstalled;
    public string InstallLabel => Result.Source == ModSource.Nexus ? "Files" : IsInstalled ? "Reinstall" : "Install";
    [RelayCommand] private Task InstallAsync() => owner.InstallResultAsync(this);
    [RelayCommand] private void OpenPage() => ModsViewModel.Open(Result.PageUrl);

    private static string Fmt(long n) => n >= 1_000_000 ? $"{n / 1e6:0.#}M" : n >= 1000 ? $"{n / 1e3:0.#}K" : n.ToString();
}

public sealed partial class ModFileRow(ModFileOption f, ModsViewModel owner) : ObservableObject
{
    public ModFileOption File { get; } = f;
    public string Label => File.Label;
    public string Description => File.Description;
    [RelayCommand] private Task InstallAsync() => owner.InstallNexusFileAsync(this);
}

public sealed class IssueRow(ModIssue i, ModsViewModel owner)
{
    public ModIssue Issue { get; } = i;
    public string Title => Issue.Title;
    public string Detail => Issue.Detail;
    public string? Fix => Issue.Fix;
    public bool HasFix => Issue.Fix is { Length: > 0 };
    public string Severity => Issue.Severity.ToString();
    public bool CanDisable => Issue.ModId is { } id && owner.Game.ModSetup.Mods.Any(m => m.Id == id && m.Enabled) && Issue.Severity != IssueSeverity.Info;
    public IRelayCommand DisableCommand => new RelayCommand(() => _ = owner.DisableByIdAsync(Issue.ModId));
}

/// <summary>The Mods window for one game: installed list, Find mods (Nexus/Thunderstore/mod.io), Check, Troubleshoot.</summary>
public sealed partial class ModsViewModel : ObservableObject
{
    private readonly AppServices _s;
    public Game Game { get; }

    public ObservableCollection<ModRow> Installed { get; } = [];
    public ObservableCollection<ModResultRow> Results { get; } = [];
    public ObservableCollection<ModFileRow> NexusFiles { get; } = [];
    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<IssueRow> Findings { get; } = [];

    [ObservableProperty] private string _tab = "Installed";
    [ObservableProperty] private ModSource _source = ModSource.Thunderstore;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _storeText = "";
    [ObservableProperty] private string _nexusFilesTitle = "";
    [ObservableProperty] private bool _hasNexusFiles;
    [ObservableProperty] private string _checkSummary = "Run a check to look for missing loaders, version mismatches, missing masters and clashes.";
    [ObservableProperty] private string _troubleSummary = "Analyse reads the last crash and the mod loaders' logs, and names the most likely mod.";
    [ObservableProperty] private string _bisectText = "";
    [ObservableProperty] private ImageSource? _hero;
    private ModSearchResult? _nexusMod;

    public ModsViewModel(AppServices s, Game game, ImageSource? hero)
    {
        _s = s;
        Game = game;
        Hero = hero;
        Source = game.ModSetup.NexusDomain is not null && s.Nexus is not null ? ModSource.Nexus : game.ModSetup.ModIoGameId is not null ? ModSource.ModIo : ModSource.Thunderstore;
        Reload();
        _ = LinkStoresAsync(quiet: true);
    }

    public string Title => $"{Game.Name} · Mods";
    public string LayoutText
    {
        get
        {
            var l = _s.Mods.Layout(Game);
            return $"{l.Framework} game · loaders: {(l.Loaders.Count == 0 ? "none" : string.Join(", ", l.Loaders))}" + (l.ScriptExtenderRuntime is { } rt ? $" ({l.ScriptExtenderName} for {rt})" : "") + $" · game version {_s.Mods.GameVersion(Game) ?? "unknown"}";
        }
    }
    public bool HasMods => Installed.Count > 0;
    public bool BisectActive => Game.ModSetup.Bisect is not null;

    public void Reload()
    {
        Installed.Clear();
        foreach (var m in Game.ModSetup.Mods.OrderBy(m => m.Priority)) Installed.Add(new ModRow(m, this));
        foreach (var r in Results) r.IsInstalled = IsInstalled(r.Result);
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(BisectActive));
        OnPropertyChanged(nameof(LayoutText));
    }

    private bool IsInstalled(ModSearchResult r) => Game.ModSetup.Mods.Any(m => m.Source == r.Source && m.SourceId == r.Id);

    private async Task Busy(string text, Func<Task> work)
    {
        IsBusy = true;
        Status = text;
        try { await work(); }
        catch (StoreException e) { Status = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or InvalidDataException or TaskCanceledException or SharpCompress.Common.SharpCompressException)
        {
            Status = $"Something went wrong: {e.Message}";
            _s.Log.Write($"[{Game.Name}] mods: {e}");
        }
        finally { IsBusy = false; }
    }

    public static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    [RelayCommand] private void ShowTab(string tab) => Tab = tab;

    // ------------------------------------------------------------------ store linking

    [RelayCommand]
    private Task LinkStoresAsync() => LinkStoresAsync(quiet: false);

    private async Task LinkStoresAsync(bool quiet)
    {
        var setup = Game.ModSetup;
        try
        {
            if (setup.ThunderstoreCommunity is null && ThunderstoreClient.Match(await _s.Thunderstore.CommunitiesAsync(), Game.Name) is { } c) setup.ThunderstoreCommunity = c.Identifier;
            if (setup.NexusDomain is null && _s.Nexus is { } nx && NexusClient.Match(await nx.GamesAsync(), Game.Name) is { } g) setup.NexusDomain = g.Domain;
            if (setup.ModIoGameId is null && _s.ModIo is { } io && (await io.FindGamesAsync(Game.Name)).FirstOrDefault() is { } mg) setup.ModIoGameId = mg.Id;
            _s.SaveLibrary();
        }
        catch (Exception e) when (e is StoreException or System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!quiet) Status = $"Couldn't reach a mod store: {e.Message}";
        }
        StoreText = string.Join(" · ", new[]
        {
            setup.NexusDomain is { } n ? $"Nexus: {n}" : _s.Nexus is null ? "Nexus: add your API key in Settings" : "Nexus: not found",
            setup.ThunderstoreCommunity is { } t ? $"Thunderstore: {t}" : "Thunderstore: not listed",
            setup.ModIoGameId is { } id ? $"mod.io: #{id}" : _s.ModIo is null ? "mod.io: add a key in Settings" : "mod.io: not found",
        });
    }

    // ------------------------------------------------------------------ find

    [RelayCommand]
    private Task SearchAsync() => Busy("Searching…", async () =>
    {
        Results.Clear();
        NexusFiles.Clear();
        HasNexusFiles = false;
        List<ModSearchResult> found;
        switch (Source)
        {
            case ModSource.Nexus:
                if (_s.Nexus is not { } nx) throw new StoreException("Add your Nexus API key in Settings → Mods first (free account).");
                if (Game.ModSetup.NexusDomain is not { } domain) throw new StoreException("This game isn't on Nexus Mods (or wasn't matched). Use Link stores.");
                found = await nx.SearchAsync(domain, Query);
                break;
            case ModSource.ModIo:
                if (_s.ModIo is not { } io) throw new StoreException("Add a mod.io API key in Settings → Mods first (free).");
                if (Game.ModSetup.ModIoGameId is not { } gid) throw new StoreException("This game isn't on mod.io (or wasn't matched).");
                found = await io.SearchAsync(gid, Query);
                break;
            default:
                if (Game.ModSetup.ThunderstoreCommunity is not { } community) throw new StoreException("This game isn't on Thunderstore. Try Nexus or mod.io.");
                Status = "Loading Thunderstore's list (the first time takes a few seconds)…";
                found = await _s.Thunderstore.SearchAsync(community, Query);
                break;
        }
        foreach (var r in found) Results.Add(new ModResultRow(r, this) { IsInstalled = IsInstalled(r) });
        Status = found.Count == 0 ? "Nothing found." : $"{found.Count} result(s).";
        foreach (var row in Results.Take(30)) _ = LoadImageAsync(row);
    });

    private async Task LoadImageAsync(ModResultRow row)
    {
        if (row.Result.ImageUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") return;
        try
        {
            var bytes = await _s.StoreHttp.GetByteArrayAsync(u);
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 160;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            row.Image = img;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or NotSupportedException or TaskCanceledException or IOException) { }
    }

    public Task InstallResultAsync(ModResultRow row) => row.Result.Source switch
    {
        ModSource.Nexus => ShowNexusFilesAsync(row.Result),
        ModSource.Thunderstore => InstallThunderstoreAsync(row.Result),
        _ => InstallDirectAsync(row.Result),
    };

    private Task ShowNexusFilesAsync(ModSearchResult mod) => Busy("Reading the mod's files…", async () =>
    {
        var nx = _s.Nexus ?? throw new StoreException("Add your Nexus API key in Settings → Mods.");
        var domain = Game.ModSetup.NexusDomain!;
        _nexusMod = mod;
        NexusFiles.Clear();
        foreach (var f in await nx.FilesAsync(domain, long.Parse(mod.Id))) NexusFiles.Add(new ModFileRow(f, this));
        NexusFilesTitle = $"{mod.Name}: pick a file";
        HasNexusFiles = NexusFiles.Count > 0;
        Status = (await nx.ValidateAsync()).IsPremium
            ? "Premium account: Install downloads straight into the launcher."
            : "Free account: Install opens the Nexus files page. Click \"Mod Manager Download\" there and the launcher installs it (turn on Nexus links in Settings → Mods).";
    });

    public Task InstallNexusFileAsync(ModFileRow file) => Busy($"Installing {file.File.Name}…", async () =>
    {
        var nx = _s.Nexus ?? throw new StoreException("Add your Nexus API key in Settings → Mods.");
        var mod = _nexusMod!;
        var domain = Game.ModSetup.NexusDomain!;
        if (!(await nx.ValidateAsync()).IsPremium)
        {
            Open(NexusClient.FilesUrl(domain, mod.Id) + $"&file_id={file.File.FileId}");
            Status = "Opened Nexus. Click \"Mod Manager Download\" and the launcher takes it from there.";
            return;
        }
        var url = await nx.DownloadUrlAsync(domain, long.Parse(mod.Id), long.Parse(file.File.FileId));
        var path = await _s.Mods.DownloadAsync(_s.StoreHttp, url, file.File.FileName ?? $"{mod.Id}-{file.File.FileId}.zip", new Progress<double>(p => Status = $"Downloading {file.File.Name}… {p:P0}"), CancellationToken.None);
        await InstallFileAsync(path, new InstalledMod
        {
            Source = ModSource.Nexus, SourceId = mod.Id, FileId = file.File.FileId, Name = mod.Name, Version = file.File.Version ?? mod.Version,
            Author = mod.Author, PageUrl = mod.PageUrl, Summary = mod.Summary,
        });
    });

    private Task InstallThunderstoreAsync(ModSearchResult mod) => Busy($"Installing {mod.Name}…", async () =>
    {
        var community = Game.ModSetup.ThunderstoreCommunity!;
        var chain = await _s.Thunderstore.WithDependenciesAsync(community, mod);
        var layout = _s.Mods.Layout(Game);
        foreach (var p in chain)
        {
            var isPack = p.Id.EndsWith("-BepInExPack", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("BepInExPack", StringComparison.OrdinalIgnoreCase);
            if (p != mod && (IsInstalled(p) || (isPack && layout.Loaders.Contains("BepInEx")))) continue;
            Status = $"Downloading {p.Name}…";
            var file = await _s.Mods.DownloadAsync(_s.StoreHttp, p.DownloadUrl!, p.FileName ?? $"{p.Id}.zip", new Progress<double>(x => Status = $"Downloading {p.Name}… {x:P0}"), CancellationToken.None);
            await InstallFileAsync(file, new InstalledMod
            {
                Source = ModSource.Thunderstore, SourceId = p.Id, Name = p.Name, Version = p.Version, Author = p.Author, PageUrl = p.PageUrl, Summary = p.Summary,
                Dependencies = p.Dependencies.Select(ModCompatibility.DependencyKey).ToList(),
            }, packageName: p.Id, confirm: false);
            layout = _s.Mods.Layout(Game);
        }
        Status = chain.Count > 1 ? $"Installed {mod.Name} with {chain.Count - 1} dependency(ies)." : $"Installed {mod.Name}.";
    });

    private Task InstallDirectAsync(ModSearchResult mod) => Busy($"Installing {mod.Name}…", async () =>
    {
        if (mod.DownloadUrl is not { } url) throw new StoreException("This mod has no downloadable file.");
        var file = await _s.Mods.DownloadAsync(_s.StoreHttp, url, mod.FileName ?? $"{mod.Id}.zip", new Progress<double>(x => Status = $"Downloading {mod.Name}… {x:P0}"), CancellationToken.None);
        var deps = Game.ModSetup.ModIoGameId is { } gid && _s.ModIo is { } io ? await io.DependenciesAsync(gid, long.Parse(mod.Id)) : [];
        await InstallFileAsync(file, new InstalledMod
        {
            Source = mod.Source, SourceId = mod.Id, Name = mod.Name, Version = mod.Version, Author = mod.Author, PageUrl = mod.PageUrl, Summary = mod.Summary, Dependencies = deps,
        });
        if (mod.Source == ModSource.ModIo) Status += " Note: some mod.io games load mods from their own in-game menu instead of the game folder.";
    });

    [RelayCommand]
    private async Task AddFromFileAsync()
    {
        var dlg = new OpenFileDialog { Title = $"Pick a mod archive for {Game.Name}", Filter = "Mod archives|*.zip;*.7z;*.rar|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        await Busy("Installing…", () => InstallFileAsync(dlg.FileName, new InstalledMod { Source = ModSource.Local, Name = Path.GetFileNameWithoutExtension(dlg.FileName) }));
    }

    /// <summary>Plan → confirm if the folder was a guess → install → re-check.</summary>
    public async Task InstallFileAsync(string path, InstalledMod meta, string? packageName = null, bool confirm = true)
    {
        var plan = await Task.Run(() => _s.Mods.Plan(Game, path, packageName ?? meta.Name));
        if (plan.Files.Count == 0) throw new StoreException("Nothing in that archive could be installed.");
        if (confirm && !plan.Confident)
        {
            var answer = MessageBox.Show($"{string.Join(" ", plan.Notes)}\n\nInstall {plan.Files.Count} file(s) into the game folder anyway? (You can remove it again from the list.)",
                "Fitz Launcher · check where this mod goes", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) { Status = "Cancelled."; return; }
        }
        var installed = await Task.Run(() => _s.Mods.Install(Game, path, plan, meta));
        Reload();
        var issues = await Task.Run(() => _s.Mods.Check(Game));
        ShowIssues(issues);
        var mine = issues.Where(i => i.ModId == installed.Id && i.Severity == IssueSeverity.Error).ToList();
        Status = $"Installed {installed.Name} → {plan.TargetLabel}." + (mine.Count > 0 ? $" Problem: {mine[0].Title}. See Check." : "");
    }

    // ------------------------------------------------------------------ installed list

    public Task ToggleAsync(ModRow row, bool on) => Busy(on ? $"Turning on {row.Name}…" : $"Turning off {row.Name}…", async () =>
    {
        await Task.Run(() => _s.Mods.SetEnabled(Game, row.Mod, on));
        row.Refresh();
        Status = on ? $"{row.Name} is on." : $"{row.Name} is off; its files are out of the game.";
    });

    public Task MoveAsync(ModRow row, int dir) => Busy("Reordering…", async () =>
    {
        await Task.Run(() => _s.Mods.Move(Game, row.Mod, dir));
        Reload();
        Status = "Later mods win when two mods change the same file.";
    });

    public Task RemoveAsync(ModRow row) => Busy($"Removing {row.Name}…", async () =>
    {
        await Task.Run(() => _s.Mods.Remove(Game, row.Mod));
        Reload();
        Status = $"Removed {row.Name}. Any game files it replaced are back.";
    });

    public Task DisableByIdAsync(Guid? id) =>
        Installed.FirstOrDefault(r => r.Mod.Id == id) is { } row ? ToggleAsync(row, false).ContinueWith(_ => Application.Current.Dispatcher.Invoke(Reload)) : Task.CompletedTask;

    [RelayCommand]
    private Task RemoveAllAsync()
    {
        if (MessageBox.Show($"Remove all {Installed.Count} mods from {Game.Name} and put back the original game files?", "Fitz Launcher", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        return Busy("Removing all mods…", async () =>
        {
            await Task.Run(() => _s.Mods.RemoveAll(Game));
            Reload();
            Status = "All mods removed. The game is back to its original files.";
        });
    }

    [RelayCommand] private void OpenModsFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_s.Mods.GameRoot(Game)}\"") { UseShellExecute = true });

    // ------------------------------------------------------------------ check & troubleshoot

    private void ShowIssues(List<ModIssue> issues)
    {
        Issues.Clear();
        foreach (var i in issues) Issues.Add(new IssueRow(i, this));
        foreach (var r in Installed) r.HasIssue = issues.Any(i => i.ModId == r.Mod.Id && i.Severity != IssueSeverity.Info);
        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        CheckSummary = errors + warnings == 0 ? "Looks good: no problems found." : $"{errors} problem(s) and {warnings} warning(s).";
    }

    [RelayCommand]
    private Task CheckAsync() => Busy("Checking mods…", async () =>
    {
        ShowIssues(await Task.Run(() => _s.Mods.Check(Game)));
        Status = CheckSummary;
    });

    private string? _report;

    [RelayCommand]
    private Task AnalyseAsync() => Busy("Reading crash info and logs…", async () =>
    {
        var (compat, logs) = await Task.Run(() => (_s.Mods.Check(Game), _s.Mods.Logs(Game)));
        var trouble = await Task.Run(() => _s.Mods.Troubleshoot(Game, compat, logs));
        Findings.Clear();
        foreach (var f in trouble.Findings) Findings.Add(new IssueRow(f, this));
        TroubleSummary = $"{trouble.Summary} ({logs.Count} log file(s) read.)";
        _report = _s.Mods.Report(Game, compat, trouble, logs);
        Status = trouble.Summary;
    });

    [RelayCommand]
    private async Task CopyReportAsync()
    {
        if (_report is null) await AnalyseAsync();
        if (_report is null) return;
        Clipboard.SetText(_report);
        var file = Path.Combine(_s.Paths.Root, "reports", $"{ModInstallPlanner.SafeName(Game.Name)}-{DateTime.Now:yyyyMMdd-HHmm}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, _report);
        Status = $"Report copied to the clipboard (user name hidden) and saved to {file}. Paste it on the mod page or a forum.";
    }

    // Find the bad mod
    private async Task ApplyStep(BisectStep step)
    {
        await Task.Run(() => _s.Mods.ApplyEnableSet(Game, step.EnableSet));
        _s.SaveLibrary();
        BisectText = step.Message;
        Reload();
    }

    [RelayCommand]
    private Task StartBisectAsync()
    {
        if (Game.ModSetup.Mods.Count(m => m.Enabled) < 2) { Status = "Needs at least two mods turned on."; return Task.CompletedTask; }
        return Busy("Setting up the first test…", () => ApplyStep(ModBisect.Start(Game.ModSetup)));
    }

    [RelayCommand] private Task BisectCrashedAsync() => Busy("Narrowing down…", () => ApplyStep(ModBisect.Report(Game.ModSetup, crashed: true)));
    [RelayCommand] private Task BisectFineAsync() => Busy("Narrowing down…", () => ApplyStep(ModBisect.Report(Game.ModSetup, crashed: false)));
    [RelayCommand] private Task StopBisectAsync() => Busy("Putting your mods back…", () => ApplyStep(ModBisect.Cancel(Game.ModSetup)));
}

/// <summary>The Mods tab: every game with its mods at a glance, plus Nexus link status.</summary>
public sealed partial class ModsOverviewViewModel(AppServices s, Action<Game> openMods) : ObservableObject
{
    public ObservableCollection<ModsOverviewRow> Rows { get; } = [];
    [ObservableProperty] private string _summary = "";
    public string NexusText => s.NexusKey is null ? "Nexus Mods isn't connected. Add your free API key in Settings → Mods to search Nexus."
        : s.Settings.NxmHandler ? "Nexus is connected and \"Mod Manager Download\" links open in the launcher." : "Nexus is connected. Turn on Nexus links in Settings → Mods to install from the site's Mod Manager Download button.";

    public void Reload()
    {
        Rows.Clear();
        foreach (var g in s.Library.Games.Where(g => !g.Hidden).OrderByDescending(g => g.ModSetup.Mods.Count).ThenByDescending(g => g.LastPlayed))
            Rows.Add(new ModsOverviewRow(g, () => openMods(g)));
        var total = s.Library.Games.Sum(g => g.ModSetup.Mods.Count);
        Summary = total == 0 ? "No mods yet. Open a game to find and install some." : $"{total} mods across {s.Library.Games.Count(g => g.ModSetup.Mods.Count > 0)} games.";
        OnPropertyChanged(nameof(NexusText));
    }
}

public sealed class ModsOverviewRow(Game g, Action open)
{
    public string Name => g.Name;
    public string Detail => g.ModSetup.Mods.Count == 0 ? "No mods" : $"{g.ModSetup.Mods.Count(m => m.Enabled)} on · {g.ModSetup.Mods.Count(m => !m.Enabled)} off" + (g.Trainer is not null ? " · trainer set up" : "") + (g.ModSetup.Bisect is not null ? " · finding a bad mod" : "");
    public IRelayCommand OpenCommand { get; } = new RelayCommand(open);
}
