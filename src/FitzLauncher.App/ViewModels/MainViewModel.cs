using System.Collections;
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

public enum Page { Library, Hardware, Sessions, Settings }

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
    public AppServices Services => _s;

    /// <summary>Raised when a temperature alert should be shown on top of the game.</summary>
    public event Action<TempAlert>? TempAlertRaised;
    public event Action? SessionStarted;
    public event Action? SessionEnded;
    /// <summary>Asks the view to open the edit dialog; returns true if saved.</summary>
    public Func<Game, bool>? EditGameDialog { get; set; }
    public Func<string, bool>? Confirm { get; set; }
    public Action? OpenBigPicture { get; set; }

    [ObservableProperty] private Page _page = Page.Library;
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

    public MainViewModel(AppServices services)
    {
        _s = services;
        foreach (var g in _s.Library.Games) Games.Add(new GameViewModel(g));
        GamesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = Filter;
        GamesView.CustomSort = new GameSorter(Sort);
        Hardware = new HardwareViewModel(services);
        Sessions = new SessionsViewModel(services);
        Settings = new SettingsViewModel(services);
        RefreshLibraryMeta();
    }

    public bool IsEmpty => Games.Count == 0;
    public string LibraryCount => Games.Count == 1 ? "1 game" : $"{Games.Count} games";
    public bool NotElevated => !_s.IsElevated;
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
        Hardware.SetVisible(value == Page.Hardware);
    }

    [RelayCommand] private void Navigate(Page page) => Page = page;
    [RelayCommand] private void DismissToast() => HasToast = false;
    [RelayCommand] private void BigPicture() => OpenBigPicture?.Invoke();

    // ------------------------------------------------------------------ add / remove

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var added = new List<GameViewModel>();
        foreach (var path in paths)
        {
            var game = await Task.Run(() => GameImporter.FromPath(path));
            if (game is null)
            {
                ShowToast($"Couldn't find a game executable in {Path.GetFileName(path)}.");
                continue;
            }
            if (_s.Library.Games.Any(g => g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast($"{game.Name} is already in your library.");
                continue;
            }
            game.FpsTarget = Hardware.Profile?.PrimaryDisplay?.MaxRefreshHz is > 0 and < 92 ? null : _s.Settings.DefaultFpsTarget;
            game.AutoTune.Enabled = _s.Settings.AutoTuneNewGames;
            game.Status = PlayStatus.Backlog;
            _s.Library.Games.Add(game);
            var vm = new GameViewModel(game);
            Games.Add(vm);
            added.Add(vm);
        }
        if (added.Count == 0) return;
        _s.SaveLibrary();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LibraryCount));
        RefreshLibraryMeta();
        Selected = added[0];

        // Artwork in the background, a few at a time; the cards show initials until it arrives.
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(added.Select(async vm =>
        {
            await gate.WaitAsync();
            try { await FetchArtworkAsync(vm); }
            finally { gate.Release(); }
        }));
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

        var session = new GameSession(game, _s.Settings, _s.Hardware, _s.GameMode, _s.Temps, _s.Store, _s.CpuTopology);
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
            _s.SaveLibrary();
            ShowToast(SessionToast(summary) + (tune is { Length: > 0 } ? $"\nAuto-tune: {tune}" : ""));
            await BackupSavesAsync(game, "after playing");
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
        var parts = new List<string> { $"▶ {vm.Name}", $"{l.Elapsed:h\\:mm\\:ss}" };
        if (l.Fps > 0) parts.Add($"{l.Fps:0} FPS");
        if (l.OnePercentLow > 0) parts.Add($"1% low {l.OnePercentLow:0}");
        if (l.Cap is { } cap) parts.Add(l.ThermalThrottling ? $"cap {cap} (thermal guard)" : $"cap {cap}");
        if (l.CpuTempC is { } c) parts.Add($"CPU {c:0}°C");
        if (l.GpuTempC is { } g) parts.Add($"GPU {g:0}°C");
        if (l.GpuLoad is { } load) parts.Add($"GPU {load:0}%");
        if (l.BenchmarkSecondsLeft is { } b) parts.Add($"⏺ benchmark {b}s");
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
        if (s.TempAlerts > 0) text += $"  ·  ⚠ {s.TempAlerts} heat alert(s)";
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
