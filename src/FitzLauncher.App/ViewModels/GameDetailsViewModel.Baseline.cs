using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Windows;

namespace FitzLauncher.App.ViewModels;

/// <summary>Optimise's session baseline, in-game graphics/resolution advice and handheld wattage.</summary>
public sealed partial class GameDetailsViewModel
{
    [ObservableProperty] private string _baselineText = "";
    [ObservableProperty] private string _adviceHeadline = "";
    public ObservableCollection<string> AdviceSteps { get; } = [];
    public bool HasAdvice => AdviceHeadline.Length > 0;
    partial void OnAdviceHeadlineChanged(string value) => OnPropertyChanged(nameof(HasAdvice));

    private DisplayInfo? PrimaryDisplay()
    {
        var displays = HardwareScanner.CurrentDisplays();
        return displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
    }

    /// <summary>
    /// Part of Optimise: start from the best past session on this screen (render scale, power) and set the
    /// handheld power limit for the target. Returns notes for the status line.
    /// </summary>
    private List<string> ApplyBaselineAndPower(int target, DisplayInfo? screen)
    {
        var notes = new List<string>();
        var history = _s.Store.LoadSessions(Game.Id);
        if (BaselinePicker.Pick(history, target, screen?.Key) is { } b)
        {
            if (screen is not null && b.Session.DisplayKey == screen.Key)
            {
                if (!Game.DisplayProfiles.TryGetValue(screen.Key, out var p))
                    Game.DisplayProfiles[screen.Key] = p = new DisplayProfile { Label = $"{screen.Width}×{screen.Height}" };
                p.RenderScale = b.RenderScale;
            }
            // A proven power figure from that session, unless live tuning already found a lower one.
            if (b.Watts is { } w && (!Game.PowerByTarget.TryGetValue(target, out var known) || known > w)) Game.PowerByTarget[target] = w;
            BaselineText = "Baseline: " + b.Describe();
            notes.Add("using your best session as the baseline");
        }
        else BaselineText = history.Any(h => h.HasFpsData)
            ? $"No session has held {target} FPS yet; play one and Optimise picks up the settings that work."
            : "Play a session first: Optimise then uses the settings and power that held your target as the baseline.";

        // Handheld (APU) wattage: set at launch, tuned live, the previous limit put back afterwards.
        if (_s.Hardware?.IsApu == true)
        {
            if (RyzenAdjClient.Find(_s.Settings.RyzenAdjPath) is null)
                notes.Add("install RyzenAdj (Settings → Power) to let the launcher set the wattage");
            else
            {
                if (!_s.Settings.PowerControl) { _s.Settings.PowerControl = true; _s.SaveSettings(); }
                Game.PowerAuto = true;
                notes.Add($"{PowerTuner.WattsFor(Game, _s.Settings, target)} W for {target} FPS at launch");
                OnPropertyChanged(nameof(ShowPower));
                OnPropertyChanged(nameof(PowerOn));
                OnPropertyChanged(nameof(PowerText));
            }
        }
        LoadAdvice(screen);
        return notes;
    }

    /// <summary>In-game graphics and resolution advice from the last session on this screen.</summary>
    private void LoadAdvice(DisplayInfo? screen = null)
    {
        AdviceSteps.Clear();
        AdviceHeadline = "";
        screen ??= PrimaryDisplay();
        if (screen is null || Game.FpsTarget is not { } target) return;
        var target2 = DisplayPlanner.EffectiveCap(target, screen.MaxRefreshHz) ?? target;
        var last = _s.Store.LoadSessions(Game.Id).Where(x => x.HasFpsData && x.Duration.TotalMinutes >= 2 && (x.DisplayKey is null || x.DisplayKey == screen.Key))
            .OrderByDescending(x => x.End).FirstOrDefault();
        if (GraphicsAdvisor.Advise(last, target2, screen.Width, screen.Height) is not { } a) return;
        AdviceHeadline = a.Headline;
        foreach (var step in a.Steps) AdviceSteps.Add(step);
    }
}
