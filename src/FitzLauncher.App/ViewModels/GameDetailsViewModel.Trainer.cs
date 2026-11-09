using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services.Mods;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Trainers;
using Microsoft.Win32;

namespace FitzLauncher.App.ViewModels;

public sealed partial class CheatRow(TrainerCheat c, Action save) : ObservableObject
{
    public string Hotkey => c.Hotkey;
    public string Name => c.Name;
    public bool Favourite { get => c.Favourite; set { c.Favourite = value; save(); OnPropertyChanged(); } }
    public bool OnAtLaunch { get => c.OnAtLaunch; set { c.OnAtLaunch = value; save(); OnPropertyChanged(); } }
    /// <summary>Only keys Fitz can press: shown greyed out otherwise.</summary>
    public bool Manual => c.Manual;
    public TrainerCheat Cheat => c;
    public bool CanSend => FitzLauncher.Core.Input.HotkeyParser.Parse(c.Hotkey) is { } k && !FitzLauncher.Core.Input.HotkeyParser.IsTrainerMasterSwitch(k);
}

public sealed class TrainerResultRow(TrainerSearchResult r, GameDetailsViewModel owner)
{
    public string Title => r.Title;
    public IAsyncRelayCommand PickCommand { get; } = new AsyncRelayCommand(() => owner.OpenTrainerResultAsync(r));
}

public sealed class TrainerDownloadRow(TrainerDownload d, string match, GameDetailsViewModel owner)
{
    public string Label => d.Label;
    public string Match => match;
    public IAsyncRelayCommand DownloadCommand { get; } = new AsyncRelayCommand(() => owner.DownloadTrainerAsync(d));
}

/// <summary>Game page: Mods summary and the FLiNG trainer for this game.</summary>
public sealed partial class GameDetailsViewModel
{
    /// <summary>Set by the main window: opens the Mods window for this game.</summary>
    public Action<Game>? OpenModsAction { get; init; }

    public string ModsSummary
    {
        get
        {
            var mods = Game.ModSetup.Mods;
            if (mods.Count == 0) return "No mods yet. Search Nexus Mods, Thunderstore or mod.io.";
            return $"{mods.Count(m => m.Enabled)} on · {mods.Count(m => !m.Enabled)} off" + (Game.ModSetup.Bisect is not null ? " · finding a bad mod" : "");
        }
    }

    [RelayCommand] private void OpenMods() => OpenModsAction?.Invoke(Game);

    // ------------------------------------------------------------------ trainer

    public ObservableCollection<CheatRow> Cheats { get; } = [];
    public ObservableCollection<TrainerResultRow> TrainerResults { get; } = [];
    public ObservableCollection<TrainerDownloadRow> TrainerDownloads { get; } = [];
    [ObservableProperty] private string _trainerQuery = "";
    [ObservableProperty] private bool _isFindingTrainer;
    [ObservableProperty] private string _trainerPageTitle = "";
    private TrainerPage? _trainerPage;
    private string? _trainerPageUrl;

    public bool HasTrainer => Game.Trainer is not null;
    public string TrainerName => Game.Trainer?.Name ?? "";
    public string TrainerMatchText
    {
        get
        {
            if (Game.Trainer is not { } t) return "";
            var v = _s.Mods.GameVersion(Game);
            return FlingParser.Check(t.SupportedVersions, v) switch
            {
                VersionMatch.Likely => $"Supports your game version ({v})",
                VersionMatch.Mismatch => $"Made for {t.SupportedVersions}; your game is {v}. It may not work.",
                _ => t.SupportedVersions is { } sv ? $"Made for {sv}" : "Supported game versions unknown",
            };
        }
    }

    public bool TrainerReturnToGame { get => Game.Trainer?.ReturnToGameAfterApply ?? true; set { if (Game.Trainer is { } t) { t.ReturnToGameAfterApply = value; _s.SaveLibrary(); } } }
    public bool TrainerLaunchWithGame { get => Game.Trainer?.LaunchWithGame ?? false; set { if (Game.Trainer is { } t) { t.LaunchWithGame = value; _s.SaveLibrary(); } } }
    public bool TrainerCloseWithGame { get => Game.Trainer?.CloseWithGame ?? true; set { if (Game.Trainer is { } t) { t.CloseWithGame = value; _s.SaveLibrary(); } } }
    public int TrainerDelay { get => Game.Trainer?.DelaySeconds ?? 10; set { if (Game.Trainer is { } t) { t.DelaySeconds = Math.Clamp(value, 0, 300); _s.SaveLibrary(); } } }

    private void LoadTrainer()
    {
        Cheats.Clear();
        foreach (var c in Game.Trainer?.Cheats ?? []) Cheats.Add(new CheatRow(c, _s.SaveLibrary));
        if (TrainerQuery.Length == 0) TrainerQuery = Game.Name;
        OnPropertyChanged(nameof(HasTrainer));
        OnPropertyChanged(nameof(TrainerName));
        OnPropertyChanged(nameof(TrainerMatchText));
        OnPropertyChanged(nameof(TrainerLaunchWithGame));
        OnPropertyChanged(nameof(TrainerCloseWithGame));
        OnPropertyChanged(nameof(TrainerDelay));
        OnPropertyChanged(nameof(ModsSummary));
    }

    [ObservableProperty] private string _newCheatHotkey = "";
    [ObservableProperty] private string _newCheatName = "";

    /// <summary>Adds an option by hand (a hotkey and a name), ticked to switch on at launch.</summary>
    [RelayCommand]
    private void AddCheat()
    {
        if (Game.Trainer is not { } t) return;
        if (TrainerOptions.AddManual(t.Cheats, NewCheatHotkey, NewCheatName) is { } problem) { Status = problem; return; }
        _s.SaveLibrary();
        Status = $"Added {NewCheatName.Trim()}: it will be switched on at launch.";
        NewCheatHotkey = NewCheatName = "";
        ReloadCheats();
    }

    [RelayCommand]
    private void RemoveCheat(CheatRow row)
    {
        if (Game.Trainer is not { } t) return;
        t.Cheats.Remove(row.Cheat);
        _s.SaveLibrary();
        ReloadCheats();
    }

    private void ReloadCheats()
    {
        Cheats.Clear();
        foreach (var c in Game.Trainer?.Cheats ?? []) Cheats.Add(new CheatRow(c, _s.SaveLibrary));
    }

    /// <summary>Reads the option list again from the trainer's FLiNG page (found by name if it wasn't set up from the page), keeping your ticks.</summary>
    [RelayCommand]
    private Task RefreshCheatsAsync() => TrainerWork("Reading the option list from FLiNG…", async () =>
    {
        if (Game.Trainer is not { } t) return;
        var url = t.PageUrl;
        if (url is null)
        {
            var found = await _s.Trainers.SearchAsync(Game.Name);
            url = found.FirstOrDefault()?.Url ?? throw new StoreException("Couldn't find this game's trainer page on FLiNG. Add the options by hand below.");
        }
        var page = await _s.Trainers.PageAsync(url);
        if (page.Cheats.Count == 0) throw new StoreException("That FLiNG page doesn't list the options in a way Fitz can read. Add them by hand below.");
        t.PageUrl ??= url;
        t.Cheats = TrainerOptions.Merge(t.Cheats, page.Cheats);
        _s.SaveLibrary();
        ReloadCheats();
        Status = $"{t.Cheats.Count} options listed. Tick \"On at launch\" for the on/off ones you always use.";
    });

    private async Task TrainerWork(string busy, Func<Task> work)
    {
        await Busy(busy, async () =>
        {
            try { await work(); }
            catch (StoreException e) { Status = e.Message; }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or SharpCompress.Common.SharpCompressException)
            {
                Status = $"Trainer: {e.Message}";
            }
        });
    }

    [RelayCommand]
    private Task FindTrainerAsync() => TrainerWork("Searching FLiNG…", async () =>
    {
        IsFindingTrainer = true;
        TrainerResults.Clear();
        TrainerDownloads.Clear();
        TrainerPageTitle = "";
        foreach (var r in await _s.Trainers.SearchAsync(TrainerQuery.Length > 0 ? TrainerQuery : Game.Name)) TrainerResults.Add(new TrainerResultRow(r, this));
        Status = TrainerResults.Count == 0 ? "No FLiNG trainer found under that name. Try a shorter name." : "Pick the trainer for this game.";
    });

    public Task OpenTrainerResultAsync(TrainerSearchResult r) => TrainerWork($"Reading {r.Title}…", async () =>
    {
        _trainerPage = await _s.Trainers.PageAsync(r.Url);
        _trainerPageUrl = r.Url;
        TrainerPageTitle = _trainerPage.Title;
        TrainerDownloads.Clear();
        var v = _s.Mods.GameVersion(Game);
        foreach (var d in _trainerPage.Downloads)
        {
            var match = FlingParser.Check(d.Versions, v) switch
            {
                VersionMatch.Likely => "matches your game",
                VersionMatch.Mismatch => $"not your version ({v})",
                _ => "version unknown",
            };
            TrainerDownloads.Add(new TrainerDownloadRow(d, match, this));
        }
        Status = TrainerDownloads.Count == 0 ? "No download link found on that page. Use Open page and download it there, then Use a downloaded trainer." : $"{_trainerPage.Cheats.Count} cheats listed. Pick the version that matches your game.";
    });

    public Task DownloadTrainerAsync(TrainerDownload d) => TrainerWork($"Downloading {d.Label}…", async () =>
    {
        var exe = await _s.Trainers.DownloadAsync(Game, d);
        SaveTrainer(exe, d.Label, d.Versions);
        Status = $"Trainer ready: {Game.Trainer!.Name}. It opens {Game.Trainer.DelaySeconds} s after the game starts.";
    });

    private void SaveTrainer(string exe, string? name, string? versions)
    {
        var old = Game.Trainer;
        Game.Trainer = new TrainerConfig
        {
            Name = name ?? Path.GetFileNameWithoutExtension(exe), ExePath = exe, PageUrl = _trainerPageUrl ?? old?.PageUrl,
            SupportedVersions = versions ?? old?.SupportedVersions,
            Cheats = _trainerPage?.Cheats is { Count: > 0 } c ? c : old?.Cheats ?? [],
            Notes = _trainerPage?.Notes, LaunchWithGame = old?.LaunchWithGame ?? true, DelaySeconds = old?.DelaySeconds ?? 10, CloseWithGame = old?.CloseWithGame ?? true,
        };
        // Keep favourites ticked before an update.
        foreach (var ch in Game.Trainer.Cheats)
        {
            ch.Favourite = ch.Favourite || old?.Cheats.Any(o => o.Favourite && o.Hotkey == ch.Hotkey) == true;
            ch.OnAtLaunch = ch.OnAtLaunch || old?.Cheats.Any(o => o.OnAtLaunch && o.Hotkey == ch.Hotkey && o.Name == ch.Name) == true; // keep your options across trainer updates
        }
        _s.SaveLibrary();
        IsFindingTrainer = false;
        LoadTrainer();
    }

    [RelayCommand]
    private Task UseTrainerFileAsync()
    {
        var dlg = new OpenFileDialog { Title = "Pick the trainer (.exe) or the archive it came in", Filter = "Trainer|*.exe;*.zip;*.7z;*.rar|All files|*.*" };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        return TrainerWork("Setting up the trainer…", async () =>
        {
            var exe = await Task.Run(() => _s.Trainers.UseFile(Game, dlg.FileName));
            SaveTrainer(exe, TrainerPageTitle.Length > 0 ? TrainerPageTitle : null, FlingParser.VersionRange(Path.GetFileName(dlg.FileName)));
            Status = "Trainer ready. Tick the cheats you use most; they show in the overlay when it starts.";
        });
    }

    [RelayCommand]
    private void OpenTrainerPage()
    {
        var url = _trainerPageUrl ?? Game.Trainer?.PageUrl ?? FlingParser.SearchUrl + Uri.EscapeDataString(Game.Name);
        if (FlingParser.IsTrustedUrl(url)) ModsViewModel.Open(url);
    }

    [RelayCommand]
    private void RemoveTrainer()
    {
        Game.Trainer = null;
        _s.SaveLibrary();
        LoadTrainer();
        Status = "Trainer removed from this game (its files stay in the launcher's trainers folder).";
    }

    [RelayCommand] private void CancelFindTrainer() => IsFindingTrainer = false;

    // ------------------------------------------------------------------ controller

    private ControllerProfileViewModel? _controllerVm;
    public ControllerProfileViewModel? ControllerVm => Game.Controller is { } c ? _controllerVm ??= new ControllerProfileViewModel(c, _s.SaveLibrary) : null;

    /// <summary>Own controller settings for this game (starts as a copy of the default profile).</summary>
    public bool UseCustomController
    {
        get => Game.Controller?.Enabled == true;
        set
        {
            if (value && Game.Controller is null) Game.Controller = _s.Settings.DefaultController.Clone();
            if (Game.Controller is { } c) c.Enabled = value;
            _s.SaveLibrary();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ControllerVm));
            OnPropertyChanged(nameof(ControllerText));
        }
    }

    public string ControllerText => UseCustomController
        ? "These settings apply while this game runs, through a virtual controller (needs ViGEmBus and HidHide: see Settings → Controllers)."
        : _s.Settings.DefaultController.Enabled ? "Using your default controller settings (Settings → Controllers)." : "The game gets your controller exactly as it is.";
}
