using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Emulation;
using FitzLauncher.Core.Models;

namespace FitzLauncher.App.ViewModels;

public sealed partial class PlatformRow(EmuPlatform platform, string name) : ObservableObject
{
    public EmuPlatform Platform { get; } = platform;
    public string Name { get; } = name;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
    public string CountText => Count == 0 ? "" : Count.ToString();
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));
}

public sealed partial class EmulatorRow(EmulationViewModel owner, EmulatorSource source, string? path) : ObservableObject
{
    public EmulatorSource Source { get; } = source;
    public string Name => Source.Name;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Status))] [NotifyPropertyChangedFor(nameof(Installed))] private string? _path = path;
    public bool Installed => Path is not null;
    public string Platforms => string.Join(", ", Source.Platforms.Select(p => FitzLauncher.Core.Emulation.Platforms.Get(p).Name));
    public string Status => Path ?? "Not found yet. Use Find emulators or Browse.";
    public string? Note => Source.SupersededReason;
    public bool HasNote => Source.SupersededReason is not null;
    [RelayCommand] private void Browse() => owner.BrowseEmulator(this);
}

public sealed partial class ChoiceChip(string label, bool selected, Action pick) : ObservableObject
{
    public string Label { get; } = label;
    public bool IsSelected { get; } = selected;
    public IRelayCommand PickCommand { get; } = new RelayCommand(pick);
}

/// <summary>The Emulation Hub page: consoles on the left, games in the middle, the selected game's controls on the right.</summary>
public sealed partial class EmulationViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    public EmulationService Service { get; }

    public ObservableCollection<PlatformRow> PlatformRows { get; } = [];
    public ObservableCollection<GameViewModel> Games { get; } = [];
    public ObservableCollection<EmulatorRow> Emulators { get; } = [];
    public ObservableCollection<ChoiceChip> TargetChips { get; } = [];
    public ObservableCollection<ChoiceChip> EmulatorChips { get; } = [];
    public ObservableCollection<string> PerfLines { get; } = [];

    [ObservableProperty] private PlatformRow? _selectedPlatform;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] private GameViewModel? _selectedGame;
    [ObservableProperty] private bool _showEmulators;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _detailInfo = "";
    [ObservableProperty] private string _tuningText = "";
    [ObservableProperty] private string _suggestionText = "";
    [ObservableProperty] private bool _hasSuggestion;
    private EmulatorSuggestion? _suggestion;

    public bool HasSelection => SelectedGame is not null;
    public bool ShowGames => !ShowEmulators;
    public bool HasGames => Games.Count > 0;
    public string EmptyText => SelectedPlatform is null ? "Pick a console on the left."
        : $"No {SelectedPlatform.Name} games yet. Use Add game folder to point Fitz at the folder holding them.";

    public EmulationViewModel(AppServices s, MainViewModel main)
    {
        _s = s;
        _main = main;
        Service = new EmulationService(s);
        foreach (var p in Platforms.All) PlatformRows.Add(new PlatformRow(p.Platform, p.Name));
        SelectedPlatform = PlatformRows[0];
        PlatformRows[0].IsSelected = true;
    }

    partial void OnShowEmulatorsChanged(bool value) => OnPropertyChanged(nameof(ShowGames));
    partial void OnSelectedPlatformChanged(PlatformRow? value) { Reload(); }
    partial void OnSelectedGameChanged(GameViewModel? value) => LoadDetail();

    [RelayCommand]
    private void PickPlatform(PlatformRow row)
    {
        foreach (var p in PlatformRows) p.IsSelected = p == row;
        SelectedPlatform = row;
        ShowEmulators = false;
    }

    public void Reload()
    {
        var keep = SelectedGame?.Game.Id;
        foreach (var row in PlatformRows) row.Count = _main.Games.Count(g => g.Game.Platform == row.Platform);
        Games.Clear();
        if (SelectedPlatform is { } sel)
            foreach (var g in _main.Games.Where(g => g.Game.Platform == sel.Platform).OrderBy(g => g.Name)) Games.Add(g);
        SelectedGame = Games.FirstOrDefault(g => g.Game.Id == keep) ?? Games.FirstOrDefault();

        Emulators.Clear();
        foreach (var src in Service.Sources) Emulators.Add(new EmulatorRow(this, src, Service.ExePath(src.Id)));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(EmptyText));
        LoadDetail();
    }

    private void LoadDetail()
    {
        TargetChips.Clear();
        EmulatorChips.Clear();
        PerfLines.Clear();
        HasSuggestion = false;
        SuggestionText = "";
        _suggestion = null;
        if (SelectedGame?.Game is not { Platform: { } platform } game) { DetailInfo = ""; TuningText = ""; return; }

        var src = Service.Source(game.EmulatorId);
        DetailInfo = $"{Platforms.Get(platform).Name} · {src?.Name ?? "no emulator"}" + (game.RomPath is { } r ? $" · {Path.GetFileName(r.TrimEnd('\\', '/'))}" : "");
        var target = Service.TargetFor(game);
        foreach (var t in Platforms.TargetOptions(platform)) TargetChips.Add(new ChoiceChip($"{t} FPS", t == target, () => SetTarget(game, t)));
        var installed = Service.InstalledIds();
        foreach (var id in Platforms.Get(platform).EmulatorIds)
            EmulatorChips.Add(new ChoiceChip(Service.Source(id)?.Name + (installed.Contains(id) ? "" : " (not set up)"), id == game.EmulatorId, () => ChangeEmulator(game, id)));

        TuningText = game.EmuTuning.Level > 0
            ? $"Quality level {game.EmuTuning.Level} of {QualityLevel.Max} (this PC's limit for this console: {Service.Ceiling(game)}): {QualityLevel.For(game.EmuTuning.Level).Describe()}." +
              (game.EmuTuning.LastNote is { } n ? $"\nLast session: {n}" : "")
            : "Not optimised yet. Optimise picks a quality for this PC and console, then keeps adjusting it after each session to hold the target.";

        foreach (var (id, rec) in game.EmuPerf.OrderByDescending(kv => kv.Value.LastPlayed))
            PerfLines.Add($"{Service.Source(id)?.Name ?? id}: {rec.AvgFps:0} FPS average, 1% low {rec.OnePercentLow:0}, {rec.TimeAtTarget:P0} on target · {rec.Sessions} session(s), {rec.Seconds / 60:0} min");
        if (PerfLines.Count == 0) PerfLines.Add("No sessions recorded yet. Play a few minutes and the results appear here, per emulator.");

        _suggestion = Service.Suggestion(game);
        HasSuggestion = _suggestion is not null;
        SuggestionText = _suggestion?.Message ?? "";
    }

    private void SetTarget(Game game, int fps)
    {
        game.FpsTarget = fps;
        _s.SaveLibrary();
        LoadDetail();
        Status = $"Target set to {fps} FPS. Press Optimise to retune for it.";
    }

    private void ChangeEmulator(Game game, string id)
    {
        if (id == game.EmulatorId) return;
        Status = Service.SwitchEmulator(game, id) ?? $"Now using {Service.Source(id)?.Name}. Press Optimise to tune it.";
        LoadDetail();
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task FindEmulatorsAsync()
    {
        Status = "Looking for emulators…";
        var added = await Service.FindEmulatorsAsync();
        Reload();
        Status = added > 0 ? $"Found {added} emulator(s). Games can now be added." : "No new emulators found. Use Browse on an emulator in the Emulators tab to point at its program.";
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (SelectedPlatform is not { } platform) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Folder holding your {platform.Name} games" };
        if (dlg.ShowDialog() != true) return;
        var key = platform.Platform.ToString();
        if (!_s.Settings.Emulation.RomFolders.TryGetValue(key, out var list)) _s.Settings.Emulation.RomFolders[key] = list = [];
        if (!list.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) list.Add(dlg.FolderName);
        _s.SaveSettings();
        await RescanAsync();
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        var added = 0;
        var problems = new List<string>();
        foreach (var info in Platforms.All)
        {
            if (!_s.Settings.Emulation.RomFolders.TryGetValue(info.Platform.ToString(), out var folders) || folders.Count == 0) continue;
            var src = Service.Sources.FirstOrDefault(x => info.EmulatorIds.Contains(x.Id) && Service.ExePath(x.Id) is not null);
            if (src is null) { problems.Add(info.Name); continue; }
            var exe = Service.ExePath(src.Id)!;
            var roms = await Task.Run(() => folders.SelectMany(f => EmulatorDiscovery.ScanRoms(f, info.Platform)).ToList());
            var known = _s.Library.Games.Where(g => g.RomPath is not null).Select(g => g.RomPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = roms.Where(r => !known.Contains(r.Path)).Select(r => EmulationLibrary.Create(r, info.Platform, src, exe)).ToList();
            added += await _main.AddEmulationGamesAsync(fresh);
        }
        Reload();
        Status = (added > 0 ? $"Added {added} game(s). " : "No new games found. ") +
                 (problems.Count > 0 ? $"No emulator set up for {string.Join(", ", problems)} yet (Emulators tab)." : "");
    }

    internal void BrowseEmulator(EmulatorRow row)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Pick {row.Name}'s program", Filter = "Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog() != true) return;
        Service.SetPath(row.Source.Id, dlg.FileName);
        row.Path = dlg.FileName;
        Status = $"{row.Name} set to {dlg.FileName}";
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (SelectedGame is { } g) await _main.PlayAsync(g);
    }

    [RelayCommand]
    private async Task OptimiseAsync()
    {
        if (SelectedGame?.Game is not { } game) return;
        Status = "Optimising…";
        Status = await Task.Run(() => Service.Optimise(game));
        LoadDetail();
    }

    [RelayCommand]
    private async Task RevertAsync()
    {
        if (SelectedGame?.Game is not { } game) return;
        Status = await Task.Run(() => Service.Revert(game));
        LoadDetail();
    }

    [RelayCommand]
    private void AcceptSuggestion()
    {
        if (SelectedGame?.Game is not { } game || _suggestion is null) return;
        var to = _suggestion.ToId;
        Status = Service.SwitchEmulator(game, to) ?? $"Switched to {Service.Source(to)?.Name}. Press Optimise to tune it, then play a few sessions.";
        LoadDetail();
    }

    [RelayCommand]
    private void DismissSuggestion() { HasSuggestion = false; }

    [RelayCommand]
    private void RemoveGame()
    {
        if (SelectedGame is { } g) { _main.RemoveFromLibrary(g); Reload(); }
    }
}
