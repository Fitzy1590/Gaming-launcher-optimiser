using System.Collections;
using System.Net.Http;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Stats;
using FitzLauncher.Windows;

namespace FitzLauncher.App.ViewModels;

public enum Page { Home, Library, Storage, Hardware, Sessions, Settings, About }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private CancellationTokenSource? _sessionCts;
    private GameSession? _session;

    public ObservableCollection<GameViewModel> Games { get; } = [];
    public ListCollectionView GamesView { get; }
    public ObservableCollection<string> AllTags { get; } = [];
    public HardwareViewModel Hardware { get; }
    public SessionsViewModel Sessions { get; }
    public SettingsViewModel Settings { get; }
    public HomeViewModel Home { get; }
    public StorageViewModel Storage { get; }
    public AppServices Services => _s;

    /// <summary>Raised when a temperature alert should be shown on top of the game.</summary>
    public event Action<TempAlert>? TempAlertRaised;
    public event Action? SessionStarted;
    public event Action? SessionEnded;
    /// <summary>Asks the view to open the edit dialog; returns true if saved.</summary>
    public Func<Game, bool>? EditGameDialog { get; set; }
    public Func<string, bool>? Confirm { get; set; }
    public Action? OpenBigPicture { get; set; }

    [ObservableProperty] private Page _page = Page.Home;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private GameViewModel? _selected;
    [ObservableProperty] private GameDetailsViewModel? _details;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _liveText = "";
    [ObservableProperty] private string _toast = "";
    [ObservableProperty] private bool _hasToast;
    [ObservableProperty] private string _activity = "";
    [ObservableProperty] private LibrarySort _sort = LibrarySort.RecentlyPlayed;
    [ObservableProperty] private PlayStatus? _statusFilter;
    [ObservableProperty] private string? _tagFilter;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private string _totalsText = "";
    [ObservableProperty] private FitzLauncher.Core.Updates.ReleaseInfo? _availableUpdate;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private bool _isUpdating;

    public bool HasUpdate => AvailableUpdate is not null;
    partial void OnAvailableUpdateChanged(FitzLauncher.Core.Updates.ReleaseInfo? value) => OnPropertyChanged(nameof(HasUpdate));

    /// <summary>At start-up (if enabled): look for a newer release and show the banner.</summary>
    public async Task CheckForUpdatesOnStartAsync()
    {
        if (!_s.Settings.AutoCheckUpdates) return;
        await Task.Delay(TimeSpan.FromSeconds(3)); // let the library paint first
        var r = await _s.Updates.CheckAsync();
        if (r.Available) ShowUpdate(r.Release!);
    }

    public void ShowUpdate(FitzLauncher.Core.Updates.ReleaseInfo release)
    {
        AvailableUpdate = release;
        UpdateText = $"Fitz Launcher {release.Version} is available (you have {UpdateService.CurrentVersion}).";
    }

    [RelayCommand]
    public async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } release) return;
        if (IsPlaying) { ShowToast("Finish your game first. The update restarts the launcher."); return; }
        IsUpdating = true;
        try
        {
            var error = await _s.Updates.InstallAsync(release, new Progress<string>(m => UpdateText = $"Updating to {release.Version}: {m}"));
            if (error.Length > 0) { UpdateText = error; return; }
            UpdateText = "Restarting…";
            _s.SaveLibrary();
            _s.SaveSettings();
            Application.Current.Shutdown();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            UpdateText = "Update failed: " + e.Message;
            _s.Log.Write("Update failed: " + e);
        }
        finally { IsUpdating = false; }
    }

    [RelayCommand] private void DismissUpdate() => AvailableUpdate = null;

    public MainViewModel(AppServices services)
    {
        _s = services;
        foreach (var g in _s.Library.Games) Games.Add(new GameViewModel(g));
        GamesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = Filter;
        GamesView.CustomSort = new GameSorter(Sort);
        Hardware = new HardwareViewModel(services);
        Sessions = new SessionsViewModel(services);
        Settings = new SettingsViewModel(services) { ShowUpdate = ShowUpdate, InstallUpdate = () => InstallUpdateAsync() };
        Home = new HomeViewModel(services, this);
        Storage = new StorageViewModel(services, this);
        if (Games.Count == 0) _page = Page.Library;
        Home.Reload();
        RefreshLibraryMeta();
    }

    public bool IsEmpty => Games.Count == 0;
    public string LibraryCount => Games.Count == 1 ? "1 game" : $"{Games.Count} games";
    public bool NotElevated => !_s.IsElevated;
    public string AppVersion => $"Fitz Launcher {UpdateService.CurrentVersion}";
    public LibrarySort[] SortOptions { get; } = Enum.GetValues<LibrarySort>();

    private bool Filter(object o)
    {
        if (o is not GameViewModel g) return false;
        if (g.Game.Hidden && !ShowHidden) return false;
        if (StatusFilter is { } st && g.Game.Status != st) return false;
        if (TagFilter is { } tag && !g.Game.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return false;
        return Search.Length == 0 || g.Name.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchChanged(string value) => GamesView.Refresh();
    partial void OnStatusFilterChanged(PlayStatus? value) => GamesView.Refresh();
    partial void OnTagFilterChanged(string? value) => GamesView.Refresh();
    partial void OnShowHiddenChanged(bool value) => GamesView.Refresh();
    partial void OnSortChanged(LibrarySort value) => GamesView.CustomSort = new GameSorter(value);

    [RelayCommand] private void SetStatusFilter(string? status) =>
        StatusFilter = Enum.TryParse<PlayStatus>(status, out var s) ? s : null;

    [RelayCommand] private void SetTagFilter(string? tag) => TagFilter = TagFilter == tag ? null : tag;

    /// <summary>Tag list and hours totals (library header).</summary>
    public void RefreshLibraryMeta()
    {
        var tags = _s.Library.Games.SelectMany(g => g.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        AllTags.Clear();
        foreach (var t in tags) AllTags.Add(t);
        var totals = LibraryStats.Compute(_s.Library.Games, _s.Store.LoadSessions(), DateTimeOffset.Now);
        TotalsText = $"{LibraryStats.FormatHours(totals.TotalHours)} played  ·  {LibraryStats.FormatHours(totals.WeekHours)} this week  ·  " +
                     $"{totals.Playing} playing  ·  {totals.Completed} finished  ·  {totals.Backlog} in backlog";
        GamesView.Refresh();
    }

    partial void OnSelectedChanged(GameViewModel? value)
    {
        Details?.Deactivate();
        Details = value is null ? null : new GameDetailsViewModel(_s, value, g => _ = PlayAsync(g), RemoveGame, EditGame, RefreshLibraryMeta);
    }

    partial void OnPageChanged(Page value)
    {
        _s.SaveSettings();
        if (value == Page.Sessions) Sessions.Reload();
        if (value == Page.Home) Home.Reload();
        if (value == Page.Storage) _ = Storage.ReloadAsync();
        Hardware.SetVisible(value == Page.Hardware);
    }

    [RelayCommand] private void Navigate(Page page) => Page = page;
    [RelayCommand] private void DismissToast() => HasToast = false;
    [RelayCommand] private void BigPicture() => OpenBigPicture?.Invoke();

    // ------------------------------------------------------------------ add / remove

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var games = new List<Game>();
        foreach (var path in paths)
        {
            var game = await Task.Run(() => GameImporter.FromPath(path));
            if (game is null) ShowToast($"Couldn't find a game executable in {Path.GetFileName(path)}.");
            else games.Add(game);
        }
        await AddGamesAsync(games);
    }

    /// <summary>Adds prepared games (from a file pick, drag & drop or the Scan game folders window).</summary>
    public async Task<int> AddGamesAsync(IEnumerable<Game> games)
    {
        var added = new List<GameViewModel>();
        foreach (var game in games)
        {
            if (_s.Library.Games.Any(g => g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast($"{game.Name} is already in your library.");
                continue;
            }
            game.FpsTarget = Hardware.Profile?.PrimaryDisplay?.MaxRefreshHz is > 0 and < 92 ? null : _s.Settings.DefaultFpsTarget;
            game.AutoTune.Enabled = _s.Settings.AutoTuneNewGames;
            if (game.Status == PlayStatus.None) game.Status = PlayStatus.Backlog;
            _s.Library.Games.Add(game);
            var vm = new GameViewModel(game);
            Games.Add(vm);
            added.Add(vm);
        }
        if (added.Count == 0) return 0;
        _s.SaveLibrary();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LibraryCount));
        RefreshLibraryMeta();
        Selected = added[0];
        if (added.Count > 1) ShowToast($"Added {added.Count} games. Fetching artwork in the background…");

        // Artwork in the background, a few at a time; the cards show initials until it arrives.
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(added.Select(async vm =>
        {
            await gate.WaitAsync();
            try { await FetchArtworkAsync(vm); }
            finally { gate.Release(); }
        }));
        return added.Count;
    }

    /// <summary>At start-up: quietly look for games added to the saved game folders since last time.</summary>
    public async Task CheckLibraryFoldersAsync()
    {
        var folders = _s.Settings.LibraryFolders.Where(Directory.Exists).ToList();
        if (folders.Count == 0) return;
        var exes = _s.Library.Games.Select(g => g.ExePath).ToList();
        var dirs = _s.Library.Games.Select(g => g.InstallDir).ToList();
        var count = await Task.Run(() => folders.SelectMany(f => FitzLauncher.Core.Scanning.LibraryFolderScanner.Scan(f, exes, dirs)).Count(g => g.SuggestAdd));
        if (count > 0) ShowToast($"Found {count} new game(s) in your game folders. Click \"Scan game folders\" to review and add them.");
    }

    public async Task FetchArtworkAsync(GameViewModel vm)
    {
        Activity = $"Fetching artwork for {vm.Name}…";
        var art = await _s.Artwork.FindAsync(vm.Game.Name);
        Activity = "";
        if (art is null) return;
        vm.Game.ArtworkPath = art.CoverPath;
        vm.Game.HeroPath = art.HeroPath ?? vm.Game.HeroPath;
        vm.Game.SteamAppId ??= art.SteamAppId;
        _s.SaveLibrary();
        await vm.LoadCoverAsync();
    }

    /// <summary>Removes a game from the library without prompts (used after an uninstall).</summary>
    public void RemoveFromLibrary(GameViewModel vm)
    {
        _s.Library.Games.Remove(vm.Game);
        Games.Remove(vm);
        if (Selected == vm) Selected = null;
        _s.SaveLibrary();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LibraryCount));
        RefreshLibraryMeta();
    }

    private void RemoveGame(GameViewModel vm)
    {
        var hasChanges = vm.Game.AppliedChanges.Count > 0;
        var msg = $"Remove {vm.Name} from the library? The game files and save backups are not deleted." +
                  (hasChanges ? "\n\nThe launcher's optimisations for this game will be reverted first." : "");
        if (Confirm?.Invoke(msg) != true) return;
        if (hasChanges) _s.Tweaks.RevertAll(vm.Game.AppliedChanges, vm.Game);
        _s.Library.Games.Remove(vm.Game);
        Games.Remove(vm);
        Selected = null;
        _s.SaveLibrary();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LibraryCount));
        RefreshLibraryMeta();
    }

    private void EditGame(GameViewModel vm)
    {
        var oldArt = vm.Game.ArtworkPath;
        if (EditGameDialog?.Invoke(vm.Game) != true) return;
        _s.SaveLibrary();
        vm.Refresh();
        // "Search again" in the dialog clears the art; a rename of an art-less game also refetches.
        if (vm.Game.ArtworkPath is null) _ = FetchArtworkAsync(vm);
        else if (vm.Game.ArtworkPath != oldArt) _ = vm.LoadCoverAsync();
        RefreshLibraryMeta();
        Selected = null;
        Selected = vm;
    }

    // ------------------------------------------------------------------ play

    [RelayCommand]
    private Task PlaySelectedAsync() => Selected is null ? Task.CompletedTask : PlayAsync(Selected);

    /// <summary>Benchmark hotkey (works in-game).</summary>
    public void StartBenchmark()
    {
        if (_session?.StartBenchmark(_s.Settings.BenchmarkSeconds) != true)
            Ui(() => ShowToast(IsPlaying ? "A benchmark is already running, or FPS tracking is off." : "Start a game first, then press Ctrl+Alt+B in-game to benchmark."));
    }

    public async Task PlayAsync(GameViewModel vm)
    {
        if (IsPlaying) { ShowToast("A game is already running."); return; }
        if (!File.Exists(vm.Game.ExePath)) { ShowToast($"Can't find {vm.Game.ExePath}. Use Edit to fix the path."); return; }

        var game = vm.Game;
        IsPlaying = true;
        vm.IsRunning = true;
        LiveText = $"Starting {vm.Name}…";
        if (game.Status is PlayStatus.None or PlayStatus.Backlog) game.Status = PlayStatus.Playing;
        _sessionCts = new CancellationTokenSource();

        // A restore point before playing, in case this session corrupts a save.
        await BackupSavesAsync(game, "before playing");

        var display = await Task.Run(() => PrepareDisplay(game));
        var limiterMode = await Task.Run(() => PrepareLimiterMode(game));
        var ryzenAdj = _s.Hardware?.IsApu == true && game.PowerAuto ? _s.RyzenAdj : null;
        var session = new GameSession(game, _s.Settings, _s.Hardware, _s.GameMode, _s.Temps, _s.Store, _s.CpuTopology)
        {
            Display = display?.Display,
            ExtraArguments = display?.LaunchArguments ?? "",
            EffectiveCap = display?.EffectiveCap,
            RenderScale = display?.RenderScale ?? 100,
            LimiterMode = limiterMode,
            RyzenAdj = ryzenAdj,
            PowerWatts = ryzenAdj is null ? null : PowerTuner.WattsFor(game, _s.Settings),
            PowerMaxWatts = _s.Settings.PowerMaxWatts,
        };
        using var quick = _s.Settings.QuickSwitch ? new QuickSwitchWatcher() : null;
        if (quick is not null) quick.Step += session.ChangeTarget;
        _session = session;
        session.Status += line => { _s.Log.Write($"[{vm.Name}] {line}"); Ui(() => Activity = line); };
        session.Live += l => Ui(() => LiveText = FormatLive(vm, l));
        session.Alert += a => Ui(() => TempAlertRaised?.Invoke(a));
        session.BenchmarkCompleted += b => Ui(() => ShowToast($"Benchmark ({b.Seconds}s): avg {b.AverageFps:0} FPS · 1% low {b.OnePercentLowFps:0} · 0.1% low {b.PointOnePercentLowFps:0}" + (b.TimeAtTarget is { } t ? $" · {t:P0} on target" : "")));
        SessionStarted?.Invoke();

        try
        {
            var summary = await Task.Run(() => session.RunAsync(_s.Settings.LaunchGamesAsAdmin, _sessionCts.Token));
            game.LastPlayed = summary.Start;
            game.TotalPlayTime += summary.Duration;
            if (summary.HasFpsData || game.LastSession is null) game.LastSession = summary;
            var tune = await Task.Run(() => AutoTune(game, summary));
            var notes = new List<string?> { tune, DisplayPlanner.Learn(game, summary, display?.EffectiveCap ?? game.FpsTarget),
                PowerTuner.Learn(game, summary, _s.Settings), PacingTuner.Learn(game, summary) };
            if (session.FinalTarget is { } kept && kept != game.FpsTarget) notes.Add(await Task.Run(() => KeepTarget(game, kept)));
            tune = string.Join(" ", notes.Where(n => !string.IsNullOrEmpty(n)));
            if (tune.Length == 0) tune = null;
            await RefreshAchievementsAsync(game);
            _s.SaveLibrary();
            ShowToast(SessionToast(summary) + (tune is { Length: > 0 } ? $"\n{tune}" : ""));
            await BackupSavesAsync(game, "after playing");
            await Task.Run(_s.SyncUpload);
        }
        catch (Exception e)
        {
            _s.Log.Write($"[{vm.Name}] launch failed: {e}");
            ShowToast($"Couldn't launch {vm.Name}: {e.Message}");
        }
        finally
        {
            _session = null;
            IsPlaying = false;
            vm.IsRunning = false;
            LiveText = "";
            Activity = "";
            vm.Refresh();
            RefreshLibraryMeta();
            if (Details?.Card == vm) Details.Reload();
            SessionEnded?.Invoke();
        }
    }

    /// <summary>
    /// Detects the active screen (handheld panel or monitor) and applies its resolution/render scale.
    /// Unreal changes go through the tweak engine, so "Revert all" restores the game's own settings.
    /// </summary>
    private DisplayPlan? PrepareDisplay(Game game)
    {
        if (!_s.Settings.AutoDisplay || !game.AutoDisplay) return null;
        try
        {
            var displays = HardwareScanner.CurrentDisplays();
            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            if (primary is null) return null;
            game.LastScan ??= _s.Scanner.Scan(game);
            var history = _s.Store.LoadSessions(game.Id);
            var plan = DisplayPlanner.Plan(game, game.LastScan, primary, history, _s.Settings.AllowLowerOutputResolution);
            if (plan.Actions.Count > 0)
            {
                var r = _s.Tweaks.Apply(game.AppliedChanges, new Tweak
                {
                    Id = DisplayPlanner.TweakId, Title = "Resolution for the active screen", Description = plan.Summary,
                    Category = TweakCategory.Graphics, Actions = plan.Actions,
                }, game);
                if (!r.Success) _s.Log.Write($"[{game.Name}] display: {r.Message}");
            }
            Ui(() => Activity = plan.Summary);
            _s.Log.Write($"[{game.Name}] display: {plan.Summary}");
            return plan;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _s.Log.Write($"[{game.Name}] display detection failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Quick switch from the game page / Big Picture / Ctrl+Alt+Up/Down while playing.</summary>
    public void QuickStep(int direction) => _session?.ChangeTarget(direction);

    /// <summary>
    /// Writes the RTSS limiter mode to test (or the one that won) into the game's RTSS profile.
    /// Only when this RTSS version stores the mode as SyncLimiter (checked in its Global profile).
    /// </summary>
    private int? PrepareLimiterMode(Game game)
    {
        if (!_s.Settings.AutoLimiterMode || game.FpsTarget is null || _s.Hardware is not { RtssInstalled: true, RtssPath: { } rtss }) return null;
        if (!RtssClient.SupportsLimiterMode(rtss)) return null;
        var mode = PacingTuner.Next(game);
        var exe = Path.GetFileName(game.LastScan?.RenderExePath ?? game.ExePath);
        var r = _s.Tweaks.Apply(game.AppliedChanges, new Tweak
        {
            Id = "rtss-limiter-mode", Title = "RTSS limiter mode", Description = PacingTuner.Name(mode), Category = TweakCategory.FrameCap,
            Actions = [new IniSetAction { FilePath = Path.Combine(rtss, "Profiles", exe + ".cfg"), Section = "Framerate", Key = "SyncLimiter", Value = mode.ToString() }],
        }, game);
        return r.Success ? mode : null;
    }

    /// <summary>Keeps a target picked in-game: it becomes the game's target and its frame cap is re-applied.</summary>
    private string KeepTarget(Game game, int target)
    {
        game.FpsTarget = target;
        game.LastScan ??= _s.Scanner.Scan(game);
        var cap = _s.BuildPlan(game).Tweaks.FirstOrDefault(t => t.Id == OptimizationPlanner.FrameCapId);
        if (cap is { IsAutomatic: true }) _s.Tweaks.Apply(game.AppliedChanges, cap, game);
        return $"Kept {target} FPS as this game's target (chosen in-game).";
    }

    public Task RefreshAchievementsAsync(Game game) => _s.RefreshAchievementsAsync(game);

    /// <summary>Runs the auto-tune step for a finished session. Returns a message, or null if nothing happened.</summary>
    private string? AutoTune(Game game, SessionSummary summary)
    {
        if (!game.AutoTune.Enabled || game.FpsTarget is null || !summary.HasFpsData) return null;
        try
        {
            game.LastScan ??= _s.Scanner.Scan(game);
            var plan = _s.BuildPlan(game);
            var decision = AutoTuner.Evaluate(game, summary, plan);
            if (decision.ToRevert is { } r) _s.Tweaks.RevertAll(game.AppliedChanges, game, r.TweakId);
            if (decision.ToApply is { } t)
            {
                var result = _s.Tweaks.Apply(game.AppliedChanges, t, game);
                if (!result.Success)
                {
                    var step = game.AutoTune.Steps.Last();
                    step.Outcome = AutoTuneOutcome.Skipped;
                    return $"couldn't apply \"{t.Title}\": {result.Message}";
                }
            }
            return decision.Kind == AutoTuneActionKind.None && decision.ToRevert is null ? null : decision.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _s.Log.Write($"[{game.Name}] auto-tune: {e}");
            return null;
        }
    }

    public async Task BackupSavesAsync(Game game, string when)
    {
        var o = _s.Settings.Backups;
        if (!o.Enabled || !game.BackupSaves || game.SaveDirs.Count == 0) return;
        try
        {
            var r = await Task.Run(() => _s.SaveBackups.Backup(game, _s.BackupTargets(), o.KeepPerGame));
            if (r.Created || r.CopiedTo is { Count: > 0 }) Activity = $"Saves {when}: {r.Message}";
            _s.SaveLibrary();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _s.Log.Write($"[{game.Name}] save backup failed: {e}");
            ShowToast($"Save backup failed for {game.Name}: {e.Message}");
        }
    }

    private static string FormatLive(GameViewModel vm, LiveStats l)
    {
        var parts = new List<string> { vm.Name, $"{l.Elapsed:h\\:mm\\:ss}" };
        if (l.Fps > 0) parts.Add($"{l.Fps:0} FPS");
        if (l.OnePercentLow > 0) parts.Add($"1% low {l.OnePercentLow:0}");
        if (l.Cap is { } cap) parts.Add(l.ThermalThrottling ? $"cap {cap} (thermal guard)" : $"cap {cap}");
        if (l.CpuTempC is { } c) parts.Add($"CPU {c:0}°C");
        if (l.GpuTempC is { } g) parts.Add($"GPU {g:0}°C");
        if (l.GpuLoad is { } load) parts.Add($"GPU {load:0}%");
        if (l.BenchmarkSecondsLeft is { } b) parts.Add($"benchmark {b}s");
        return string.Join("   ·   ", parts);
    }

    private static string SessionToast(SessionSummary s)
    {
        var dur = s.Duration.TotalHours >= 1 ? $"{(int)s.Duration.TotalHours}h {s.Duration.Minutes}m" : $"{Math.Max(1, s.Duration.Minutes)}m";
        var text = $"{s.GameName}: {dur} played";
        if (s.HasFpsData)
        {
            text += $"  ·  Avg {s.AverageFps:0} FPS  ·  1% low {s.OnePercentLowFps:0}";
            if (s.TimeAtTarget is { } t) text += $"  ·  {t:P0} on target";
        }
        if (s.MaxGpuTempC is { } g) text += $"  ·  GPU max {g:0}°C";
        if (s.MaxCpuTempC is { } c) text += $"  ·  CPU max {c:0}°C";
        if (s.TempAlerts > 0) text += $"  ·  {s.TempAlerts} heat alert(s)";
        return text;
    }

    public void ShowToast(string text)
    {
        Toast = text;
        HasToast = true;
    }

    public void StopSession() => _sessionCts?.Cancel();

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    /// <summary>Favourites first, then the chosen order.</summary>
    private sealed class GameSorter(LibrarySort sort) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not GameViewModel a || y is not GameViewModel b) return 0;
            if (a.Game.Favourite != b.Game.Favourite) return a.Game.Favourite ? -1 : 1;
            return LibraryStats.Compare(a.Game, b.Game, sort);
        }
    }
}
