using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Models;
using FitzLauncher.Windows;

namespace FitzLauncher.App.ViewModels;

public enum Page { Library, Hardware, Sessions, Settings }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private CancellationTokenSource? _sessionCts;

    public ObservableCollection<GameViewModel> Games { get; } = [];
    public ICollectionView GamesView { get; }
    public HardwareViewModel Hardware { get; }
    public SessionsViewModel Sessions { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>Raised when a temperature alert should be shown on top of the game.</summary>
    public event Action<TempAlert>? TempAlertRaised;
    public event Action? SessionStarted;
    public event Action? SessionEnded;
    /// <summary>Asks the view to open the edit dialog; returns true if saved.</summary>
    public Func<Game, bool>? EditGameDialog { get; set; }
    public Func<string, bool>? Confirm { get; set; }

    [ObservableProperty] private Page _page = Page.Library;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private GameViewModel? _selected;
    [ObservableProperty] private GameDetailsViewModel? _details;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _liveText = "";
    [ObservableProperty] private string _toast = "";
    [ObservableProperty] private bool _hasToast;
    [ObservableProperty] private string _activity = "";

    public MainViewModel(AppServices services)
    {
        _s = services;
        foreach (var g in _s.Library.Games.OrderByDescending(g => g.Favourite).ThenByDescending(g => g.LastPlayed ?? g.AddedAt))
            Games.Add(new GameViewModel(g));
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = o => o is GameViewModel g && (Search.Length == 0 || g.Name.Contains(Search, StringComparison.OrdinalIgnoreCase));
        Hardware = new HardwareViewModel(services);
        Sessions = new SessionsViewModel(services);
        Settings = new SettingsViewModel(services);
    }

    public bool IsEmpty => Games.Count == 0;
    public string LibraryCount => Games.Count == 1 ? "1 game" : $"{Games.Count} games";
    public bool NotElevated => !_s.IsElevated;

    partial void OnSearchChanged(string value) => GamesView.Refresh();

    partial void OnSelectedChanged(GameViewModel? value) =>
        Details = value is null ? null : new GameDetailsViewModel(_s, value, g => _ = PlayAsync(g), RemoveGame, EditGame);

    partial void OnPageChanged(Page value)
    {
        _s.SaveSettings();
        if (value == Page.Sessions) Sessions.Reload();
        Hardware.SetVisible(value == Page.Hardware);
    }

    [RelayCommand] private void Navigate(Page page) => Page = page;

    [RelayCommand] private void DismissToast() => HasToast = false;

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
            _s.Library.Games.Add(game);
            var vm = new GameViewModel(game);
            Games.Insert(0, vm);
            added.Add(vm);
        }
        if (added.Count == 0) return;
        _s.SaveLibrary();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LibraryCount));
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
        vm.Game.SteamAppId = art.SteamAppId ?? vm.Game.SteamAppId;
        _s.SaveLibrary();
        await vm.LoadCoverAsync();
    }

    private void RemoveGame(GameViewModel vm)
    {
        var hasChanges = vm.Game.AppliedChanges.Count > 0;
        var msg = $"Remove {vm.Name} from the library? The game files are not deleted." +
                  (hasChanges ? "\n\nThe launcher's optimisations for this game will be reverted first." : "");
        if (Confirm?.Invoke(msg) != true) return;
        if (hasChanges) _s.Tweaks.RevertAll(vm.Game.AppliedChanges, vm.Game);
        _s.Library.Games.Remove(vm.Game);
        Games.Remove(vm);
        Selected = null;
        _s.SaveLibrary();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LibraryCount));
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
        Selected = null;
        Selected = vm;
    }

    // ------------------------------------------------------------------ play

    [RelayCommand]
    private Task PlaySelectedAsync() => Selected is null ? Task.CompletedTask : PlayAsync(Selected);

    public async Task PlayAsync(GameViewModel vm)
    {
        if (IsPlaying) { ShowToast("A game is already running."); return; }
        if (!File.Exists(vm.Game.ExePath)) { ShowToast($"Can't find {vm.Game.ExePath}. Use Edit to fix the path."); return; }

        IsPlaying = true;
        vm.IsRunning = true;
        LiveText = $"Starting {vm.Name}…";
        _sessionCts = new CancellationTokenSource();
        var session = new GameSession(vm.Game, _s.Settings, _s.Hardware, _s.GameMode, _s.Temps, _s.Store);
        session.Status += line => { _s.Log.Write($"[{vm.Name}] {line}"); Ui(() => Activity = line); };
        session.Live += l => Ui(() => LiveText = FormatLive(vm, l));
        session.Alert += a => Ui(() => TempAlertRaised?.Invoke(a));
        SessionStarted?.Invoke();

        try
        {
            var summary = await Task.Run(() => session.RunAsync(_s.Settings.LaunchGamesAsAdmin, _sessionCts.Token));
            vm.Game.LastPlayed = summary.Start;
            vm.Game.TotalPlayTime += summary.Duration;
            if (summary.HasFpsData || vm.Game.LastSession is null) vm.Game.LastSession = summary;
            _s.SaveLibrary();
            ShowToast(SessionToast(summary));
        }
        catch (Exception e)
        {
            _s.Log.Write($"[{vm.Name}] launch failed: {e}");
            ShowToast($"Couldn't launch {vm.Name}: {e.Message}");
        }
        finally
        {
            IsPlaying = false;
            vm.IsRunning = false;
            LiveText = "";
            Activity = "";
            vm.Refresh();
            if (Details?.Card == vm) Details.LoadHistory();
            SessionEnded?.Invoke();
        }
    }

    private static string FormatLive(GameViewModel vm, LiveStats l)
    {
        var parts = new List<string> { $"▶ {vm.Name}", $"{l.Elapsed:h\\:mm\\:ss}" };
        if (l.Fps > 0) parts.Add($"{l.Fps:0} FPS");
        if (l.CpuTempC is { } c) parts.Add($"CPU {c:0}°C");
        if (l.GpuTempC is { } g) parts.Add($"GPU {g:0}°C");
        if (l.GpuLoad is { } load) parts.Add($"GPU {load:0}%");
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
}
