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

    // ---- auto TDP (handhelds)

    /// <summary>"Default" (the global setting), "Off", "Observe" or "On".</summary>
    public string AutoTdpChoice
    {
        get => Game.AutoTdp?.ToString() ?? "Default";
        set
        {
            Game.AutoTdp = Enum.TryParse<AutoTdpMode>(value, out var m) ? m : null;
            if (Game.AutoTdp is AutoTdpMode.On or AutoTdpMode.Observe) Game.PowerAuto = true; // auto TDP needs this game's power managed
            _s.SaveLibrary();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoTdpText));
            OnPropertyChanged(nameof(PowerOn));
        }
    }

    // ---- closed-loop telemetry tuner (SPEC §12)

    /// <summary>"Default" (the global setting), "Off", "WatchOnly" or "On".</summary>
    public string ClosedLoopChoice
    {
        get => Game.ClosedLoop?.ToString() ?? "Default";
        set
        {
            Game.ClosedLoop = Enum.TryParse<FitzLauncher.Core.Telemetry.ClosedLoopMode>(value, out var m) ? m : null;
            _s.SaveLibrary();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ClosedLoopText));
        }
    }

    public string ClosedLoopText =>
        $"Now: {FitzLauncher.Core.Telemetry.ClosedLoopAdvice.Name(Game.ClosedLoop ?? _s.Settings.ClosedLoop)}. See the Telemetry page for what it does. Applies from the next time you play.";

    public string AutoTdpText
    {
        get
        {
            var mode = Game.AutoTdp ?? _s.Settings.AutoTdp;
            var learned = Game.AutoTdpProfiles.Count == 0 ? ""
                : " Learned: " + string.Join(" · ", Game.AutoTdpProfiles.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} FPS → {kv.Value.Text}")) + ".";
            return mode switch
            {
                AutoTdpMode.On => $"On: holds {Game.FpsTarget?.ToString() ?? "your"} FPS with the least power ({_s.Settings.AutoTdpMinWatts}–{_s.Settings.AutoTdpMaxWatts} W).{learned}",
                AutoTdpMode.Observe => "Watching only: nothing is changed; a log of what it would do is written for each session.",
                _ => "Off for this game." + learned,
            };
        }
    }

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
