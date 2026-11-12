using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Stats;

namespace FitzLauncher.App.ViewModels;

/// <summary>A library card. Cover art loads lazily in the background.</summary>
public sealed partial class GameViewModel : ObservableObject
{
    public Game Game { get; }

    public GameViewModel(Game game)
    {
        Game = game;
        _ = LoadCoverAsync();
    }

    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private bool _isRunning;

    public string Name => Game.Name;
    public string Initials => string.Concat(Game.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public bool HasCover => Cover is not null;

    public string FpsBadge => Game.LastSession is { HasFpsData: true } s ? $"{s.AverageFps:0} FPS" : "";
    public bool HasFpsBadge => FpsBadge.Length > 0;
    public bool IsOptimized => Game.OptimizedAt is not null && Game.AppliedChanges.Count > 0;
    private MainStoryProgress? Story => MainStoryProgress.For(Game);
    public double ProgressValue => Story?.Percent ?? 0;
    public bool HasProgress => Story is { Percent: > 0 };
    public string ProgressRingText => Story?.RingText ?? "";
    public string ProgressLabel => Story is { Percent: > 0 } p ? $"{p.PercentText} main story" : "";
    public double CardOpacity => Game.Hidden ? 0.45 : 1;
    public bool IsFavourite => Game.Favourite;
    public string TargetText => Game.FpsTarget is { } t ? $"{t} FPS target" : "Uncapped";
    public string PlayTimeText => Game.TotalPlayTime.TotalMinutes < 1 ? "Not played yet"
        : Game.TotalPlayTime.TotalHours >= 1 ? $"{Game.TotalPlayTime.TotalHours:0.#} h played" : $"{Game.TotalPlayTime.TotalMinutes:0} min played";

    public async Task LoadCoverAsync()
    {
        ImageLoader.Invalidate(Game.ArtworkPath);
        Cover = await ImageLoader.LoadAsync(Game.ArtworkPath, 300);
        OnPropertyChanged(nameof(HasCover));
    }

    /// <summary>Re-raise everything derived from the Game model after it changes.</summary>
    public bool HasUpdate => Game.Updates.State is FitzLauncher.Core.Models.UpdateState.Available or FitzLauncher.Core.Models.UpdateState.ProbablyAvailable;
    public string Grade => Game.ReportGrade ?? "";
    public bool HasGrade => Game.ReportGrade is not null;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
