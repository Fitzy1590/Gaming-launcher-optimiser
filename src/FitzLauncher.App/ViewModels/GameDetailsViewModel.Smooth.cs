using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.App.ViewModels;

/// <summary>One Smooth mode suggestion on the game page, with an Apply / Undo button.</summary>
public sealed partial class SmoothRow(SmoothSuggestion s, GameDetailsViewModel owner) : ObservableObject
{
    public string Title => s.Title;
    public string Why => s.Why;
    public string ButtonLabel => s.Applied ? "Undo" : "Apply";
    [ObservableProperty] private string _result = "";

    [RelayCommand]
    private void Toggle() => owner.ToggleSmooth(s, this);
}

/// <summary>Game page: Smooth mode (better 1% lows in heavy scenes) and its one-click suggestions.</summary>
public sealed partial class GameDetailsViewModel
{
    public ObservableCollection<SmoothRow> SmoothRows { get; } = [];
    public ObservableCollection<string> SmoothAdvice { get; } = [];
    [ObservableProperty] private string _smoothResult = "";

    public bool SmoothModeOn
    {
        get => Game.SmoothMode;
        set
        {
            if (Game.SmoothMode == value) return;
            SmoothResult = SmoothMode.SetEnabled(Game, _s.Settings, value, LastFpsSession(), _s.Hardware?.IsApu == true, _s.Hardware);
            SaveSmooth();
            OnPropertyChanged();
            RefreshSmooth();
            SyncTargetSelection();
        }
    }

    private SessionSummary? LastFpsSession() =>
        _s.Store.LoadSessions(Game.Id).Where(x => x.HasFpsData).OrderByDescending(x => x.Start).FirstOrDefault();

    private void SaveSmooth() { _s.SaveLibrary(); _s.SaveSettings(); }

    private void RefreshSmooth()
    {
        var last = LastFpsSession();
        SmoothRows.Clear();
        foreach (var s in SmoothMode.Suggest(Game, _s.Settings, last, _s.Hardware?.IsApu == true, _s.Hardware)) SmoothRows.Add(new SmoothRow(s, this));
        SmoothAdvice.Clear();
        foreach (var a in SmoothMode.Advice(last)) SmoothAdvice.Add(a);
    }

    public void ToggleSmooth(SmoothSuggestion s, SmoothRow row)
    {
        var done = s.Applied ? SmoothMode.Undo(Game, _s.Settings, s.Fix) : SmoothMode.Apply(Game, _s.Settings, s.Fix, LastFpsSession(), _s.Hardware);
        SaveSmooth();
        SmoothResult = done ?? "Already set: nothing more to change here.";
        RefreshSmooth();
        SyncTargetSelection();
    }
}
