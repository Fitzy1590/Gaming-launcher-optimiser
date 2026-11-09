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

public enum Page { Home, Library, Mods, Storage, Hardware, Sessions, Settings, About, Emulation }

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
    public EmulationViewModel Emulation { get; }
    public ModsOverviewViewModel ModsOverview { get; }
    public AppServices Services => _s;

    /// <summary>Raised when a temperature alert should be shown on top of the game.</summary>
    public event Action<TempAlert>? TempAlertRaised;
    public event Action? SessionStarted;
    /// <summary>The device went to sleep (true) or woke (false).</summary>
    public event Action<bool>? DeviceSleepChanged;

    public bool SessionOwnsPower => _session?.OwnsPowerLimit == true;

    /// <summary>PS + Right: hide or show the FPS counters in the game that's running. Only for that game: the next one starts with them showing.</summary>
    public void ToggleOverlayFromPad()
    {
        if (_session is { } s) s.ToggleOverlayHidden();
        else ShowToast("PS + Right hides or shows the FPS counters while a game is running.");
    }
    private bool _consoleWasRunning;

    /// <summary>Low-power sleep: stop the launcher's own polling while asleep, start it again on wake.</summary>
    /// <summary>After an auto TDP session: keep the power range that held the target (live mode) and report what happened.</summary>
    private string? LearnAutoTdp(Game game, GameSession session, SessionSummary summary)
    {
        if (session.AutoTdpLearned is { } p && session.AutoTdpTarget is { } target && summary.Duration.TotalMinutes >= 5 && session.AutoTdp == AutoTdpMode.On)
        {
            game.AutoTdpProfiles.TryGetValue(target, out var old);
            game.AutoTdpProfiles[target] = AutoTdpProfile.Blend(old, p);
            // Keep the older single-number memory in step, so the rest of the app shows a sensible power figure too.
            game.PowerByTarget[target] = p.Typical;
            game.PowerWatts = p.Typical;
        }
        if (session.AutoTdpReport is { } report)
        {
            _s.Settings.AutoTdpLastNote = $"{game.Name}: {report}";
            _s.SaveSettings();
        }
        return session.AutoTdpReport;
    }

    public void OnDeviceSleep(bool asleep)
    {
        FitzLauncher.Windows.PlayStationPads.Shared.Paused = asleep;
        if (asleep)
        {
            _consoleWasRunning = _s.ConsoleButton.Running;
            if (_consoleWasRunning) _s.ConsoleButton.Stop();
        }
        else
        {
            if (_consoleWasRunning) _s.ConsoleButton.Start();
            // The firmware resets power limits on wake; a running game puts its own back.
            if (_session?.OwnsPowerLimit == true) _session.OnResume();
        }
        DeviceSleepChanged?.Invoke(asleep);
    }
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
        if (!r.Available) return;
        ShowUpdate(r.Release!);
        // Auto-install: never mid-game (the update restarts the launcher); the banner stays as a fallback.
        if (_s.Settings.AutoInstallUpdates && !IsPlaying && !IsUpdating)
        {
            ShowToast($"Updating to {r.Release!.Version}. The launcher will restart in a moment.");
            await InstallUpdateAsync();
        }
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
        Emulation = new EmulationViewModel(services, this);
        ModsOverview = new ModsOverviewViewModel(services, OpenMods);
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
        Details = value is null ? null : new GameDetailsViewModel(_s, value, g => _ = PlayAsync(g), RemoveGame, EditGame, RefreshLibraryMeta) { OpenModsAction = OpenMods, IsPlayingCheck = () => IsPlaying };
        TintToGame(value);
    }

    private GameViewModel? _tintWaiting;

    /// <summary>The UI takes its accent and background tint from the selected game's cover.</summary>
    private void TintToGame(GameViewModel? game)
    {
        if (_tintWaiting is not null) _tintWaiting.PropertyChanged -= CoverLoaded;
        _tintWaiting = null;
        FitzLauncher.App.Services.GameTheme.Enabled = _s.Settings.MatchGameColours && !SafeMode;
        if (game is null) return;
        if (game.Cover is null) { _tintWaiting = game; game.PropertyChanged += CoverLoaded; return; }
        FitzLauncher.App.Services.GameTheme.Apply(game.Cover);
    }

    private void CoverLoaded(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameViewModel.Cover) || sender != Selected) return;
        TintToGame(Selected);
    }

    partial void OnPageChanged(Page value)
    {
        _s.SaveSettings();
        if (value == Page.Sessions) Sessions.Reload();
        if (value == Page.Home) Home.Reload();
        if (value == Page.Mods) ModsOverview.Reload();
        if (value == Page.Storage) _ = Storage.ReloadAsync();
        if (value == Page.Emulation) Emulation.Reload();
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

    /// <summary>Adds emulator games (they share one program, so they are told apart by game file, not by exe).</summary>
    public async Task<int> AddEmulationGamesAsync(IEnumerable<Game> games)
    {
        var added = new List<GameViewModel>();
        foreach (var game in games)
        {
            if (_s.Library.Games.Any(g => g.RomPath is not null && g.RomPath.Equals(game.RomPath, StringComparison.OrdinalIgnoreCase))) continue;
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
        if (vm.Game.Platform is not null && await Task.Run(() => Emulation.Service.PrepareLaunch(vm.Game)) is { } emuProblem) ShowToast(emuProblem);

        // Mods: repair what can be repaired, then ask before launching with problems left.
        if (!SafeMode && _s.Settings.ModLaunchCheck && vm.Game.ModSetup.Mods.Any(m => m.Enabled))
        {
            var (fixedCount, problems) = await Task.Run(() => _s.Mods.PreLaunchCheck(vm.Game));
            if (fixedCount > 0) ShowToast($"Mods: repaired {fixedCount} problem(s) before launch.");
            if (problems.Count > 0 && Confirm is { } ask &&
                !ask($"Mod problems in {vm.Name}:\n\n" + string.Join("\n", problems.Take(5).Select(p => $"• {p.Title}: {p.Detail}" + (p.Fix is { } f ? $" ({f})" : ""))) +
                     (problems.Count > 5 ? $"\n…and {problems.Count - 5} more." : "") + "\n\nPlay anyway? Choose No to open the mods page."))
            {
                OpenMods(vm.Game);
                return;
            }
        }

        var game = vm.Game;
        IsPlaying = true;
        vm.IsRunning = true;
        LiveText = $"Starting {vm.Name}…";
        if (game.Status is PlayStatus.None or PlayStatus.Backlog) game.Status = PlayStatus.Playing;
        _sessionCts = new CancellationTokenSource();

        // A restore point before playing, in case this session corrupts a save.
        await BackupSavesAsync(game, "before playing");

        var display = await Task.Run(() => PrepareDisplay(game));
        // FPS cap remembered for this PC and screen, then the cooling mode on top.
        var screen = display?.Display ?? await Task.Run(CurrentScreen);
        // A/B test in progress: this run uses the next setup and records a benchmark automatically.
        var abTest = game.AbTest is { Done: false } t0 ? t0 : null;
        var abSetup = abTest?.Next;
        var coolingPlan = CoolingPlan.For(abSetup?.Cooling ?? game.Cooling ?? _s.Settings.DefaultCooling);
        int? sessionCap = display?.EffectiveCap;
        if (DeviceCaps.For(game, Environment.MachineName, screen) is { } saved)
        {
            sessionCap = saved > 0 && screen is { MaxRefreshHz: > 0 } sc ? Math.Min(saved, sc.MaxRefreshHz - 3) : saved;
            if (sessionCap == game.FpsTarget) sessionCap = null;
        }
        if (coolingPlan.CapSteps > 0 && (sessionCap ?? game.FpsTarget) is { } baseCap and > 0)
            sessionCap = CoolingPlan.StepDown(baseCap, coolingPlan.CapSteps);
        if (abSetup?.Cap is { } abCap) sessionCap = abCap;
        var limiterMode = await Task.Run(() => PrepareLimiterMode(game));
        // Auto TDP (handhelds): off by default; per game or for all games. Needs RyzenAdj, whether or not "power limit" is on.
        var autoTdp = _s.Hardware?.IsApu == true && game.PowerAuto && !SafeMode ? game.AutoTdp ?? _s.Settings.AutoTdp : AutoTdpMode.Off;
        var ryzenAdj = _s.Hardware?.IsApu == true && game.PowerAuto
            ? autoTdp != AutoTdpMode.Off && RyzenAdjClient.Find(_s.Settings.RyzenAdjPath) is { } autoPath ? new RyzenAdjClient(autoPath, _s.Paths) : _s.RyzenAdj
            : null;
        var autoRequested = _s.Hardware?.IsApu == true && !SafeMode ? game.AutoTdp ?? _s.Settings.AutoTdp : AutoTdpMode.Off;
        var autoWhyNot = AutoTdpGate.WhyNot(autoRequested, _s.Hardware?.IsApu == true, SafeMode, game.PowerAuto, ryzenAdj is not null);
        if (ryzenAdj is null || autoWhyNot is not null) autoTdp = AutoTdpMode.Off;
        if (autoWhyNot is not null) ShowToast($"Auto TDP isn't running: {autoWhyNot}.");
        var autoMin = Math.Clamp(_s.Settings.AutoTdpMinWatts, 10, 120);
        var autoMax = Math.Clamp(Math.Max(_s.Settings.AutoTdpMaxWatts, autoMin), autoMin, 120);
        var autoTarget = sessionCap ?? game.FpsTarget ?? _s.Settings.DefaultFpsTarget;
        // Where auto TDP starts: what held this target last time (a little above), else the learned/default power.
        var autoStart = autoTarget is { } at && game.AutoTdpProfiles.TryGetValue(at, out var known) && known.Typical > 0
            ? (int)Math.Round(known.Typical * 1.08)
            : _s.Settings.AutoTdpStartWatts;
        autoStart = Math.Clamp(autoStart, autoMin, autoMax);
        // Undervolt ladder: first undervolted session starts one step in.
        var st = _s.Settings;
        st.UndervoltTarget = Math.Clamp(st.UndervoltTarget, -30, 0);
        if (st.UndervoltEnabled && st.UndervoltCurrent == 0 && !st.UndervoltSettled && st.UndervoltTarget < 0) st.UndervoltCurrent = -UndervoltLadder.Step;
        // Undervolt works without power control; it needs RyzenAdj and a Ryzen APU.
        var undervolt = _s.Settings.UndervoltEnabled && _s.Settings.UndervoltCurrent < 0 && _s.Hardware?.IsApu == true
                        && RyzenAdjClient.Find(_s.Settings.RyzenAdjPath) is { } uvPath ? new RyzenAdjClient(uvPath, _s.Paths) : null;
        var session = new GameSession(game, _s.Settings, _s.Hardware, _s.GameMode, _s.Temps, _s.Store, _s.CpuTopology, _s.Paths)
        {
            Display = display?.Display,
            ExtraArguments = display?.LaunchArguments ?? "",
            EffectiveCap = sessionCap,
            AutoBenchmark = abTest is null ? null : (abTest.WarmupSeconds, abTest.Seconds),
            Cooling = SafeMode || coolingPlan.Mode == CoolingMode.Off ? null : coolingPlan,
            Amd = SafeMode || _s.Hardware?.PrimaryGpu?.Vendor != GpuVendor.Amd ? null : game.Amd,
            RenderScale = display?.RenderScale ?? 100,
            LimiterMode = limiterMode,
            RyzenAdj = ryzenAdj ?? undervolt,
            UndervoltOffset = undervolt is null || SafeMode ? null : _s.Settings.UndervoltCurrent,
            Controller = SafeMode ? null : game.Controller is { Enabled: true } own ? own : _s.Settings.DefaultController is { Enabled: true } def ? def : null,
            PowerWatts = ryzenAdj is null || autoTdp == AutoTdpMode.Observe ? null // observe changes nothing
                : autoTdp == AutoTdpMode.On ? autoStart
                : Math.Max(10, (int)Math.Round(PowerTuner.WattsFor(game, _s.Settings, sessionCap ?? game.FpsTarget) * coolingPlan.PowerScale)),
            AutoTdp = autoTdp, AutoTdpMinWatts = autoMin, AutoTdpMaxWatts = autoMax,
            AutoTdpFallbackTarget = _s.Settings.DefaultFpsTarget, AutoTdpWhyNot = autoWhyNot,
            PowerMaxWatts = _s.Settings.PowerMaxWatts,
            PowerMinWatts = _s.Settings.PowerMinWatts,
            LivePowerTuning = _s.Settings.LivePowerTuning && game.PowerAuto && coolingPlan.PowerScale >= 1,
        };
        using var quick = _s.Settings.QuickSwitch || _s.Settings.QuickMenu || _s.Settings.AdaptiveCap || _s.Settings.IdleSleepInGame ? new QuickSwitchWatcher() : null;
        if (quick is not null)
        {
            quick.KeepAwakeOnInput = _s.Settings.IdleSleepInGame;
            if (_s.Settings.QuickSwitch && _s.Settings.LegacyPadShortcuts) quick.Step += session.ChangeTarget;
            if (_s.Settings.QuickMenu && _s.Settings.LegacyPadShortcuts)
            {
                quick.Hud += session.SetHud;
                quick.PowerStep += session.ChangePower;
            }
        }
        // Controller activity feeds the adaptive idle cap.
        // Xbox-style pads (watcher) and DualShock/DualSense (HID) both count as playing for the idle cap.
        FitzLauncher.Windows.PlayStationPads.Shared.Start();
        session.WithControllerInput(() =>
        {
            var sony = FitzLauncher.Windows.PlayStationPads.Shared.LastInput;
            return quick is not null && quick.LastInput > sony ? quick.LastInput : sony;
        });

        _session = session;
        session.Status += line => { _s.Log.Write($"[{vm.Name}] {line}"); Ui(() => Activity = line); };
        session.Trace += line => _s.Log.Write($"[{vm.Name}] {line}");
        // What Auto TDP is doing (or why it isn't), kept for Settings.
        session.Status += line =>
        {
            if (!line.StartsWith("Auto TDP", StringComparison.Ordinal) && !line.StartsWith("Something else keeps changing", StringComparison.Ordinal)) return;
            _s.Settings.AutoTdpLastNote = $"{vm.Name}: {line}";
            _s.SaveSettings();
        };
        session.Live += l => Ui(() => LiveText = FormatLive(vm, l));
        session.Alert += a => Ui(() => TempAlertRaised?.Invoke(a));
        // Trainer: started a little after the game, closed with it.
        System.Diagnostics.Process? trainer = null;
        var trainerCts = new CancellationTokenSource();
        if (game.Trainer is { LaunchWithGame: true } t)
        {
            if (!File.Exists(t.ExePath)) ShowToast($"Trainer not found at {t.ExePath}. Set it up again on the game page.");
            else session.Launched += () => _ = Task.Run(async () =>
            {
                trainer = await _s.Trainers.StartAsync(t, () => session.GamePids, line => { _s.Log.Write($"[{vm.Name}] {line}"); Ui(() => Activity = line); }, trainerCts.Token);
                if (trainer is null) return;
                if (t.Cheats.Where(c => c.OnAtLaunch).Select(c => c.Name).ToList() is { Count: > 0 } on) session.ShowNote($"Trainer on: {string.Join(", ", on)}", 8);
                else if (FitzLauncher.App.Services.Mods.TrainerService.FavouritesLine(t) is { Length: > 0 } keys) session.ShowNote("Trainer: " + keys, 12);
            });
        }
        // PS shortcuts and the wheel can step the FPS target and power limit of this game.
        FitzLauncher.Windows.WindowsActions.TargetStep = session.ChangeTarget;
        FitzLauncher.Windows.WindowsActions.PowerStep = session.ChangePower;
        System.Diagnostics.Process? gameHelp = null;
        if (_s.Settings.GameHelpWithGames && !SafeMode)
            session.Launched += () => _ = Task.Run(async () =>
                gameHelp = await _s.GameHelp.StartWithGameAsync(() => session.GamePids, line => { _s.Log.Write($"[{vm.Name}] {line}"); Ui(() => Activity = line); }, trainerCts.Token));
        FitzLauncher.Windows.WindowsActions.TrainerAndGame = () => (trainer is { HasExited: false } tp ? tp.Id : null, session.GamePids);
        if (abTest is not null)
            session.BenchmarkCompleted += b => Ui(() =>
            {
                var run = new AbRun { AverageFps = b.AverageFps, OnePercentLowFps = b.OnePercentLowFps, StutterCount = b.StutterCount, MaxGpuTempC = b.MaxGpuTempC };
                var which = abTest.NextName;
                if (abTest.ResultA is null) abTest.ResultA = run; else abTest.ResultB ??= run;
                _s.SaveLibrary();
                ShowToast(abTest.Done ? $"A/B test finished. {abTest.Verdict()}" : $"A/B run {which} recorded. Next time you press Play, run B ({abTest.B.Label}) starts: play the same part again.");
            });
        if (abTest is null) session.BenchmarkCompleted += b => Ui(() => ShowToast($"Benchmark ({b.Seconds}s): avg {b.AverageFps:0} FPS · 1% low {b.OnePercentLowFps:0} · 0.1% low {b.PointOnePercentLowFps:0}" + (b.TimeAtTarget is { } t ? $" · {t:P0} on target" : "")));
        SessionStarted?.Invoke();

        try
        {
            var summary = await Task.Run(() => session.RunAsync(_s.Settings.LaunchGamesAsAdmin, _sessionCts.Token));
            game.LastPlayed = summary.Start;
            game.TotalPlayTime += summary.Duration;
            if (summary.HasFpsData || game.LastSession is null) game.LastSession = summary;
            var crashNote = RecordCrashOrClean(game, summary, session.UndervoltApplied);
            var emuNote = Emulation.Service.AfterSession(game, summary);
            var tune = await Task.Run(() => AutoTune(game, summary));
            var notes = new List<string?> { tune, DisplayPlanner.Learn(game, summary, display?.EffectiveCap ?? game.FpsTarget),
                autoTdp != AutoTdpMode.Off ? LearnAutoTdp(game, session, summary)
                    : coolingPlan.PowerScale < 1 ? null
                    : session.LearnedWatts is { } lw && session.LearnedTarget is { } powerTarget ? PowerTuner.LearnLive(game, powerTarget, lw, _s.Settings)
                    : PowerTuner.Learn(game, summary, _s.Settings),
                PacingTuner.Learn(game, summary), crashNote, emuNote };
            if (session.FinalTarget is { } kept && game.CapPerDevice && screen is not null)
            {
                DeviceCaps.Save(game, Environment.MachineName, screen, kept);
                notes.Add($"Saved {kept} FPS for {game.Name} on this screen ({screen.Label}).");
            }
            else if (session.FinalTarget is { } kept2 && kept2 != game.FpsTarget) notes.Add(await Task.Run(() => KeepTarget(game, kept2)));
            tune = string.Join(" ", notes.Where(n => !string.IsNullOrEmpty(n)));
            if (tune.Length == 0) tune = null;
            await RefreshAchievementsAsync(game);
            _s.SaveLibrary();
            // Stutters worth acting on: say what caused them and where the fix is.
            var stutterNote = session.StutterReport is { PerMinute: >= 1, Causes.Count: > 0 } sr
                ? $"\nStutters ({sr.PerMinute:0.#}/min): {sr.Causes[0].Title.ToLowerInvariant()}. Open {game.Name} → Stutter detective for the fix."
                : "";
            ShowToast(SessionToast(summary) + (tune is { Length: > 0 } ? $"\n{tune}" : "") + stutterNote);
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
            trainerCts.Cancel();
            FitzLauncher.Windows.WindowsActions.TrainerAndGame = null;
            FitzLauncher.Windows.WindowsActions.TargetStep = null;
            FitzLauncher.Windows.WindowsActions.PowerStep = null;
            GameHelpService.Stop(gameHelp);
            FitzLauncher.Windows.PlayStationPads.Shared.Stop();
            if (game.Trainer is { CloseWithGame: true }) FitzLauncher.App.Services.Mods.TrainerService.Stop(trainer);
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

    /// <summary>Started with extras off after two start-up crashes in a row.</summary>
    [ObservableProperty] private bool _safeMode;

    [RelayCommand]
    private void LeaveSafeMode()
    {
        CrashReporter.ClearSafeMode();
        if (Environment.ProcessPath is { } exe) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    /// <summary>First start and after each update: check the PC; show the results only when something needs doing.</summary>
    public async Task DeviceCheckOnStartAsync()
    {
        if (_s.Settings.DeviceCheckedVersion == UpdateService.CurrentVersion.ToString()) return;
        await Task.Delay(TimeSpan.FromSeconds(2));
        var vm = new DeviceCheckViewModel(_s);
        await vm.RunAsync();
        if (vm.NeedsAttention) ShowDeviceCheck(vm);
    }

    [RelayCommand]
    private void RunDeviceCheck()
    {
        var vm = new DeviceCheckViewModel(_s);
        _ = vm.RunAsync();
        ShowDeviceCheck(vm);
    }

    private static void ShowDeviceCheck(DeviceCheckViewModel vm) =>
        new Views.DeviceCheckWindow(vm) { Owner = Application.Current.MainWindow }.Show();

    /// <summary>Once a day: fetch new official upscaler DLLs and swap them into games (in the background).</summary>
    public async Task UpscalersDailyAsync()
    {
        if (!_s.Settings.KeepUpscalersUpdated || _s.Settings.LastUpscalerCheck?.LocalDateTime.Date == DateTime.Today) return;
        await Task.Delay(TimeSpan.FromSeconds(40));
        await UpdateUpscalersAsync(manual: false);
    }

    [RelayCommand]
    private Task UpdateUpscalers() => UpdateUpscalersAsync(manual: true);

    public async Task UpdateUpscalersAsync(bool manual)
    {
        if (manual) ShowToast("Checking for new XeSS / FSR / DLSS DLLs…");
        try
        {
            var fresh = await _s.Upscalers.RefreshLibraryAsync();
            var (games, dlls) = IsPlaying && !manual ? (0, 0) : await Task.Run(() => _s.Upscalers.ApplyToLibrary(g => Games.Any(v => v.Game == g && v.IsRunning)));
            if (dlls > 0 || fresh.Count > 0 || manual)
                ShowToast((fresh.Count > 0 ? $"New: {string.Join(", ", fresh)}. " : "") +
                          (dlls > 0 ? $"Updated {dlls} upscaler DLL(s) in {games} game(s) (backed up; revert on each game's page)." : manual ? "All games already have the newest DLLs." : ""));
            Details?.Reload();
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            if (manual) ShowToast($"Couldn't update upscaler DLLs: {e.Message}");
        }
    }

    /// <summary>First launch of the day: check official sources for game updates (in the background).</summary>
    public async Task CheckGameUpdatesDailyAsync()
    {
        if (!_s.Settings.GameUpdateCheckDaily || _s.Settings.LastGameUpdateCheck?.LocalDateTime.Date == DateTime.Today) return;
        await Task.Delay(TimeSpan.FromSeconds(20)); // after the launcher's own update check and start-up work
        await CheckGameUpdatesAsync(manual: false);
    }

    [RelayCommand]
    private Task CheckGameUpdates() => CheckGameUpdatesAsync(manual: true);

    public async Task CheckGameUpdatesAsync(bool manual)
    {
        if (manual) ShowToast("Checking official versions for your games…");
        try
        {
            var found = await _s.GameUpdates.CheckAllAsync(_s.Library.Games.ToList());
            foreach (var g in Games) g.Refresh();
            Details?.Reload();
            if (found.Count > 0) ShowToast($"Game updates available: {string.Join(", ", found.Take(4).Select(g => g.Name))}{(found.Count > 4 ? $" and {found.Count - 4} more" : "")}. Open a game to see the details.");
            else if (manual) ShowToast("No game updates found (games without a Steam or GOG match can't be checked).");
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            if (manual) ShowToast($"Couldn't check game updates: {e.Message}");
        }
    }

    /// <summary>Opens the Mods window for a game.</summary>
    public void OpenMods(Game game)
    {
        var card = Games.FirstOrDefault(g => g.Game == game);
        var hero = Details?.Game == game ? Details.Hero : card?.Cover;
        var vm = new ModsViewModel(_s, game, hero);
        var w = new Views.ModsWindow(vm) { Owner = Application.Current.MainWindow };
        w.Closed += (_, _) => { Details?.Reload(); ModsOverview.Reload(); };
        w.Show();
    }

    /// <summary>
    /// A Nexus "Mod Manager Download" link: fetch the download link with the key in the link (works for free
    /// accounts), download, and install into the game linked to that Nexus domain.
    /// </summary>
    public async Task HandleNxmAsync(string url)
    {
        if (FitzLauncher.App.Services.Mods.NxmLink.Parse(url) is not { } link) return;
        if (_s.Nexus is not { } nx) { ShowToast("A Nexus download link arrived, but Nexus isn't connected. Add your API key in Settings → Mods."); return; }
        var game = Selected?.Game.ModSetup.NexusDomain == link.Domain ? Selected.Game
            : _s.Library.Games.FirstOrDefault(g => g.ModSetup.NexusDomain == link.Domain);
        if (game is null)
        {
            try
            {
                var nexusGame = (await nx.GamesAsync()).FirstOrDefault(g => g.Domain == link.Domain);
                game = nexusGame is null ? null : _s.Library.Games.FirstOrDefault(g => FitzLauncher.App.Services.Mods.NexusClient.Match([nexusGame], g.Name) is not null);
                if (game is not null) game.ModSetup.NexusDomain = link.Domain;
            }
            catch (Exception e) when (e is FitzLauncher.App.Services.Mods.StoreException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
        }
        if (game is null) { ShowToast($"Got a Nexus download for \"{link.Domain}\", but no game in your library is linked to it. Open the game's Mods window first."); return; }
        if (IsPlaying) { ShowToast($"Finish playing before installing mods into {game.Name}."); return; }

        ShowToast($"Downloading a mod for {game.Name} from Nexus…");
        try
        {
            var mod = await nx.ModAsync(link.Domain, link.ModId);
            var file = (await nx.FilesAsync(link.Domain, link.ModId)).FirstOrDefault(f => f.FileId == link.FileId.ToString());
            var cdn = await nx.DownloadUrlAsync(link.Domain, link.ModId, link.FileId, link.Key, link.Expires);
            var path = await _s.Mods.DownloadAsync(_s.StoreHttp, cdn, file?.FileName ?? $"{link.ModId}-{link.FileId}.zip", null, CancellationToken.None);
            var vm = new ModsViewModel(_s, game, Details?.Game == game ? Details.Hero : null);
            await vm.InstallFileAsync(path, new InstalledMod
            {
                Source = ModSource.Nexus, SourceId = link.ModId.ToString(), FileId = link.FileId.ToString(), Name = mod.Name,
                Version = file?.Version ?? mod.Version, Author = mod.Author, PageUrl = mod.PageUrl, Summary = mod.Summary,
            });
            ShowToast($"{game.Name}: {vm.Status}");
            Details?.Reload();
        }
        catch (Exception e) when (e is FitzLauncher.App.Services.Mods.StoreException or System.Net.Http.HttpRequestException or IOException or InvalidDataException or TaskCanceledException or SharpCompress.Common.SharpCompressException)
        {
            _s.Log.Write($"nxm install failed: {e}");
            ShowToast($"Couldn't install that Nexus download: {e.Message}");
        }
    }

    /// <summary>
    /// Crash: remember it (module, undervolt state), back the undervolt off, explain what likely caused it.
    /// Clean session (2+ minutes): this becomes the "last good" point for Roll back, and the undervolt can step on.
    /// </summary>
    private string? RecordCrashOrClean(Game game, SessionSummary summary, bool undervoltActive)
    {
        if (summary.Crashed)
        {
            var crash = new CrashInfo { When = summary.End, FaultingModule = summary.FaultingModule, UndervoltActive = undervoltActive };
            game.LastCrash = crash;
            game.CrashCount++;
            if (undervoltActive && UndervoltLadder.AfterCrash(_s.Settings) is { } uv) { _s.Log.Write(uv); _s.SaveSettings(); }
            return CrashAdvisor.Explain(game, crash);
        }
        if (summary.Duration < TimeSpan.FromMinutes(2)) return null;
        game.LastGoodSession = summary.End;
        if (!undervoltActive) return null;
        var step = UndervoltLadder.AfterCleanSession(_s.Settings);
        if (step is not null) _s.SaveSettings();
        return step;
    }

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
    /// <summary>The screen games will open on (primary display).</summary>
    public static DisplayInfo? CurrentScreen()
    {
        try
        {
            var displays = HardwareScanner.CurrentDisplays();
            return displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

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
