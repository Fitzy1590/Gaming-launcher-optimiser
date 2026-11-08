using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.App.ViewModels;

public sealed partial class CapChoice(int? value, string label, GameDetailsViewModel owner) : ObservableObject
{
    public int? Value { get; } = value;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
    public IRelayCommand PickCommand { get; } = new RelayCommand(() => owner.PickDeviceCap(value));
}

public sealed class SavedCapRow(string key, DeviceCap cap, GameDetailsViewModel owner)
{
    public string Text => $"{cap.Label}: {(cap.Cap == 0 ? "no cap" : $"{cap.Cap} FPS")}";
    public IRelayCommand RemoveCommand { get; } = new RelayCommand(() => owner.RemoveDeviceCap(key));
}

public sealed partial class CoolingChoice(CoolingMode? mode, string label, GameDetailsViewModel owner) : ObservableObject
{
    public CoolingMode? Mode { get; } = mode;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
    public IRelayCommand PickCommand { get; } = new RelayCommand(() => owner.PickCooling(mode));
}

/// <summary>Game page: FPS cap per PC and screen, and the cooling mode.</summary>
public sealed partial class GameDetailsViewModel
{
    public ObservableCollection<CapChoice> DeviceCapChoices { get; } = [];
    public ObservableCollection<SavedCapRow> SavedDeviceCaps { get; } = [];
    public ObservableCollection<CoolingChoice> CoolingChoices { get; } = [];
    private DisplayInfo? _screen;

    public bool CapPerDevice
    {
        get => Game.CapPerDevice;
        set { Game.CapPerDevice = value; _s.SaveLibrary(); LoadCooling(); }
    }

    public string CurrentScreenText => _screen is null ? "Couldn't detect the current screen." : $"This PC and screen: {DeviceCaps.Label(Environment.MachineName, _screen)}";

    private void LoadCooling()
    {
        _screen ??= MainViewModel.CurrentScreen();
        if (DeviceCapChoices.Count == 0)
        {
            DeviceCapChoices.Add(new CapChoice(null, "Game target", this));
            DeviceCapChoices.Add(new CapChoice(0, "No cap", this));
            foreach (var t in new[] { 60 }.Concat(LauncherSettings.FpsTargets)) DeviceCapChoices.Add(new CapChoice(t, t.ToString(), this));
            CoolingChoices.Add(new CoolingChoice(null, "Default", this));
            foreach (var m in Enum.GetValues<CoolingMode>()) CoolingChoices.Add(new CoolingChoice(m, m.ToString(), this));
        }
        var current = DeviceCaps.For(Game, Environment.MachineName, _screen);
        foreach (var c in DeviceCapChoices) c.IsSelected = c.Value == current;
        SavedDeviceCaps.Clear();
        foreach (var (key, cap) in Game.DeviceCaps.OrderBy(k => k.Value.Label)) SavedDeviceCaps.Add(new SavedCapRow(key, cap, this));
        foreach (var c in CoolingChoices) c.IsSelected = c.Mode == Game.Cooling;
        OnPropertyChanged(nameof(CapPerDevice));
        OnPropertyChanged(nameof(CurrentScreenText));
        OnPropertyChanged(nameof(CoolingText));
    }

    public void PickDeviceCap(int? cap)
    {
        if (_screen is null) return;
        if (cap is { } c) DeviceCaps.Save(Game, Environment.MachineName, _screen, c);
        else Game.DeviceCaps.Remove(DeviceCaps.Key(Environment.MachineName, _screen));
        Game.CapPerDevice = true;
        _s.SaveLibrary();
        LoadCooling();
        Status = cap switch
        {
            null => "This screen uses the game's FPS target.",
            0 => "No FPS cap on this screen; remembered for next time.",
            _ => $"{cap} FPS on this screen; remembered for next time.",
        };
    }

    public void RemoveDeviceCap(string key)
    {
        Game.DeviceCaps.Remove(key);
        _s.SaveLibrary();
        LoadCooling();
    }

    public string CoolingText
    {
        get
        {
            var mode = Game.Cooling ?? _s.Settings.DefaultCooling;
            var prefix = Game.Cooling is null ? $"Using the default ({mode}). " : "";
            return prefix + CoolingPlan.For(mode).Description + (_s.Hardware?.IsApu == true || mode is CoolingMode.Off or CoolingMode.Balanced ? "" : " (Power trims need an AMD APU with RyzenAdj.)");
        }
    }

    public void PickCooling(CoolingMode? mode)
    {
        Game.Cooling = mode;
        _s.SaveLibrary();
        LoadCooling();
    }
}
