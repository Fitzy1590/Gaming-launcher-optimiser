using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.App.ViewModels;

public sealed partial class TargetOption(int? value, GameDetailsViewModel owner) : ObservableObject
{
    public int? Value { get; } = value;
    public string Label => Value is { } v ? v.ToString() : "Off";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isSuggested;
    public IAsyncRelayCommand Command => owner.OptimiseAtCommand;
}

public sealed partial class GameDetailsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action<GameViewModel> _play;
    private readonly Action<GameViewModel> _remove;
    private readonly Action<GameViewModel> _edit;
    private readonly Action _refreshMeta;

    public GameViewModel Card { get; }
    public Game Game => Card.Game;

    public ObservableCollection<TargetOption> Targets { get; } = [];
    public ObservableCollection<TweakViewModel> OneClick { get; } = [];
    public ObservableCollection<TweakViewModel> Suggestions { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public ObservableCollection<string> ScanFacts { get; } = [];
    public ObservableCollection<SessionRow> History { get; } = [];

    [ObservableProperty] private ImageSource? _hero;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _suggestedTargetText = "";
    [ObservableProperty] private PointCollection _historyLine = [];
    [ObservableProperty] private PointCollection _lastTimeline = [];
    [ObservableProperty] private string _lastSessionText = "";
    [ObservableProperty] private double _targetY;
    [ObservableProperty] private bool _hasTargetLine;
    [ObservableProperty] private string _targetLabel = "";
    [ObservableProperty] private bool _hasHistory;
    [ObservableProperty] private string _appliedSummary = "";

    public GameDetailsViewModel(AppServices services, GameViewModel card, Action<GameViewModel> play, Action<GameViewModel> remove, Action<GameViewModel> edit, Action refreshMeta)
    {
        _refreshMeta = refreshMeta;
        _s = services;
        Card = card;
        _play = play;
        _remove = remove;
        _edit = edit;
        Targets.Add(new TargetOption(null, this));
        foreach (var t in LauncherSettings.FpsTargets) Targets.Add(new TargetOption(t, this));
        SyncTargetSelection();
        _ = LoadAsync();
    }

    public string Name => Game.Name;
    public string Subtitle => $"{Card.PlayTimeText}{(Game.LastPlayed is { } lp ? $" · last played {lp:d MMM}" : "")}";
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    public bool HasChanges => Game.AppliedChanges.Count > 0;

    private readonly CancellationTokenSource _alive = new();

    /// <summary>Called when another game is selected, so pending background work is skipped.</summary>
    public void Deactivate() => _alive.Cancel();

    private async Task LoadAsync()
    {
        Hero = await ImageLoader.LoadAsync(Game.HeroPath ?? Game.ArtworkPath, 900);
        LoadHistory();
        InitExtras();
        if (Game.LastScan is null)
        {
            // Don't scan games you're just scrolling past (Big Picture / keyboard).
            try { await Task.Delay(500, _alive.Token); } catch (TaskCanceledException) { return; }
            await RescanAsync();
        }
        else RebuildPlan();
        if (Game.SaveDirs.Count == 0) DetectSaves(quiet: true);
        LoadBackups();
    }

    /// <summary>After a session: history, benchmarks, auto-tune and plan may all have changed.</summary>
    public void Reload()
    {
        LoadHistory();
        LoadExtras();
        RebuildPlan();
        LoadBackups();
        Card.Refresh();
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand] private void Play() => _play(Card);
    [RelayCommand] private void Remove() => _remove(Card);
    [RelayCommand] private void Edit() => _edit(Card);

    [RelayCommand]
    private void OpenFolder()
    {
        var dir = Directory.Exists(Game.InstallDir) ? Game.InstallDir : Path.GetDirectoryName(Game.ExePath);
        if (dir is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        await Busy("Scanning game files…", async () =>
        {
            Game.LastScan = await Task.Run(() => _s.Scanner.Scan(Game));
            _s.SaveLibrary();
        });
        RebuildPlan();
    }

    /// <summary>The big "Optimise" button: uses the current target (or the suggested one).</summary>
    [RelayCommand]
    private Task OptimiseAsync() => OptimiseAtAsync(Game.FpsTarget ?? Targets.FirstOrDefault(t => t.IsSuggested)?.Value ?? _s.Settings.DefaultFpsTarget);

    /// <summary>One click on 90 / 100 / 110 / 117 / Off: set the target and apply all zero-quality-loss tweaks.</summary>
    [RelayCommand]
    private async Task OptimiseAtAsync(int? target)
    {
        var applied = 0;
        var failed = new List<string>();
        var manual = 0;
        await Busy(target is { } t ? $"Optimising for a stable {t} FPS…" : "Removing the frame cap…", async () =>
        {
            Game.FpsTarget = target;
            Game.LastScan = await Task.Run(() => _s.Scanner.Scan(Game));
            var plan = _s.BuildPlan(Game);
            await Task.Run(() =>
            {
                if (target is null) _s.Tweaks.RevertAll(Game.AppliedChanges, Game, OptimizationPlanner.FrameCapId);
                foreach (var tweak in plan.Tweaks.Where(x => x.Id == OptimizationPlanner.FrameCapId && !x.IsAutomatic)) manual++;
                foreach (var tweak in plan.OneClick)
                {
                    var r = _s.Tweaks.Apply(Game.AppliedChanges, tweak, Game);
                    if (r.Success) applied++;
                    else failed.Add($"{tweak.Title}: {r.Message}");
                }
            });
            Game.OptimizedAt = DateTimeOffset.Now;
            _s.SaveLibrary();
        });
        SyncTargetSelection();
        RebuildPlan();
        Card.Refresh();
        Status = failed.Count == 0
            ? $"Applied {applied} optimisation(s) with no visual change.{(manual > 0 ? " The frame cap needs one manual step (see below)." : "")}{(Suggestions.Any(x => x.Recommended && !x.IsApplied) ? " Recommended graphics changes are listed below." : "")}"
            : $"Applied {applied}; {failed.Count} couldn't be applied: {string.Join("; ", failed)}";
        foreach (var f in failed) _s.Log.Write($"[{Game.Name}] {f}");
    }

    /// <summary>Applies the graphics suggestions marked Recommended (only ones the launcher can apply itself).</summary>
    [RelayCommand]
    private async Task ApplyRecommendedAsync()
    {
        var list = Suggestions.Where(x => x.Recommended && x.IsAutomatic && !x.IsApplied).ToList();
        foreach (var t in list) await ApplyTweakAsync(t);
        Status = list.Count == 0 ? "Nothing to apply automatically: the recommended items are manual steps." : $"Applied {list.Count} recommended change(s). Play a session to measure the difference.";
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        var count = Game.AppliedChanges.Count;
        IReadOnlyList<string> errors = [];
        await Busy("Reverting all changes…", async () =>
        {
            errors = await Task.Run(() => _s.Tweaks.RevertAll(Game.AppliedChanges, Game));
            Game.OptimizedAt = null;
            _s.SaveLibrary();
        });
        RebuildPlan();
        Card.Refresh();
        Status = errors.Count == 0 ? $"Reverted {count} change(s). The game is back to its original settings." : "Some changes could not be reverted: " + string.Join("; ", errors);
    }

    private async Task ApplyTweakAsync(TweakViewModel t)
    {
        var r = await Task.Run(() => _s.Tweaks.Apply(Game.AppliedChanges, t.Tweak, Game));
        t.Message = r.Success ? (t.NeedsRestart ? "Applied: restart Windows to take effect" : "Applied") : r.Message;
        t.IsApplied = TweakEngine.IsApplied(Game.AppliedChanges, t.Tweak);
        _s.SaveLibrary();
        UpdateAppliedSummary();
        Card.Refresh();
    }

    private async Task RevertTweakAsync(TweakViewModel t)
    {
        var errors = await Task.Run(() => _s.Tweaks.RevertAll(Game.AppliedChanges, Game, t.Tweak.Id));
        t.Message = errors.Count == 0 ? "Reverted" : string.Join("; ", errors);
        t.IsApplied = TweakEngine.IsApplied(Game.AppliedChanges, t.Tweak);
        _s.SaveLibrary();
        UpdateAppliedSummary();
    }

    // ------------------------------------------------------------------ plan / history

    private void RebuildPlan()
    {
        if (Game.LastScan is null) return;
        var plan = _s.BuildPlan(Game);

        OneClick.Clear();
        Suggestions.Clear();
        foreach (var t in plan.OneClick) OneClick.Add(Wrap(t));
        foreach (var t in plan.Suggestions.OrderByDescending(t => t.Recommended).ThenBy(t => t.VisualImpact)) Suggestions.Add(Wrap(t));

        Notes.Clear();
        foreach (var n in plan.Notes) Notes.Add(n);

        foreach (var opt in Targets) opt.IsSuggested = opt.Value is not null && opt.Value == plan.SuggestedTarget;
        SuggestedTargetText = plan.SuggestedTargetReason ?? "";

        var scan = Game.LastScan;
        ScanFacts.Clear();
        ScanFacts.Add(EngineName(scan.Engine));
        ScanFacts.Add($"{scan.TotalBytes / 1024d / 1024 / 1024:0.0} GB · {scan.FileCount:N0} files");
        if (scan.Upscalers.Count > 0) ScanFacts.Add(string.Join(" / ", scan.Upscalers.Select(u => u switch { Upscaler.DlssFrameGen => "DLSS FG", Upscaler.Dlss => "DLSS", Upscaler.Fsr => "FSR", _ => "XeSS" })));
        if (scan.UsesDx12) ScanFacts.Add("DirectX 12");
        if (scan.UsesVulkan) ScanFacts.Add("Vulkan");
        foreach (var i in scan.Injectors) ScanFacts.Add(i);
        ScanFacts.Add(scan.ConfigDirectories.Count > 0 ? "Config found" : "No editable config");

        UpdateAppliedSummary();
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(HasNotes));
    }

    private TweakViewModel Wrap(Tweak t) =>
        new(t, ApplyTweakAsync, RevertTweakAsync) { IsApplied = TweakEngine.IsApplied(Game.AppliedChanges, t) };

    private void UpdateAppliedSummary()
    {
        AppliedSummary = Game.AppliedChanges.Count == 0 ? "No changes applied yet."
            : $"{Game.AppliedChanges.Count} setting(s) changed by the launcher" + (Game.OptimizedAt is { } at ? $" · optimised {at:d MMM HH:mm}" : "");
        OnPropertyChanged(nameof(HasChanges));
    }

    private void SyncTargetSelection()
    {
        foreach (var t in Targets) t.IsSelected = t.Value == Game.FpsTarget;
    }

    public void LoadHistory()
    {
        var sessions = _s.Store.LoadSessions(Game.Id).Where(s => s.HasFpsData).OrderByDescending(s => s.Start).ToList();
        History.Clear();
        foreach (var s in sessions.Take(12)) History.Add(new SessionRow(s));
        HasHistory = sessions.Count > 0;
        HistoryLine = Sparkline.Build(sessions.Take(20).Reverse().Select(s => s.AverageFps).ToList(), 400, 40);
        if (sessions.FirstOrDefault() is { } last)
        {
            var values = last.FpsTimeline.Select(f => (double)f).ToList();
            var (points, targetY) = Sparkline.BuildWithTarget(values, 400, 96, last.TargetFps);
            LastTimeline = points;
            HasTargetLine = targetY is not null;
            TargetY = targetY ?? 0;
            TargetLabel = last.TargetFps is { } t ? $"cap {t}" : "";
            LastSessionText = SessionRow.Describe(last);
        }
        OnPropertyChanged(nameof(Subtitle));
    }

    private async Task Busy(string text, Func<Task> work)
    {
        IsBusy = true;
        BusyText = text;
        Status = "";
        try { await work(); }
        catch (Exception e)
        {
            Status = "Something went wrong: " + e.Message;
            _s.Log.Write($"[{Game.Name}] {e}");
        }
        finally { IsBusy = false; }
    }

    private static string EngineName(GameEngine e) => e switch
    {
        GameEngine.Unreal4 => "Unreal Engine 4",
        GameEngine.Unreal5 => "Unreal Engine 5",
        GameEngine.UnrealUnknownVersion => "Unreal Engine",
        GameEngine.Source2 => "Source 2",
        GameEngine.IdTech => "id Tech",
        GameEngine.RedEngine => "REDengine",
        GameEngine.CryEngine => "CryEngine",
        GameEngine.ReEngine => "RE Engine",
        GameEngine.CreationEngine => "Creation Engine",
        GameEngine.Unknown => "Engine not recognised",
        _ => e.ToString(),
    };
}

public sealed class SessionRow(SessionSummary s)
{
    public SessionSummary Session => s;
    public string Game => s.GameName;
    public string When => s.Start.ToString("ddd d MMM, HH:mm");
    public string Duration => s.Duration.TotalHours >= 1 ? $"{(int)s.Duration.TotalHours}h {s.Duration.Minutes}m" : $"{s.Duration.Minutes}m";
    public string Avg => s.HasFpsData ? $"{s.AverageFps:0}" : "–";
    public string Low => s.HasFpsData ? $"{s.OnePercentLowFps:0}" : "–";
    public string AtTarget => s.TimeAtTarget is { } t ? $"{t:P0}" : "–";
    public string Temps => $"{(s.MaxCpuTempC is { } c ? $"CPU {c:0}°" : "")}{(s.MaxGpuTempC is { } g ? $"  GPU {g:0}°" : "")}".Trim();
    public string Target => s.TargetFps is { } t2 ? t2.ToString() : "–";
    public bool HadAlerts => s.TempAlerts > 0;

    public static string Describe(SessionSummary s)
    {
        if (!s.HasFpsData) return "No FPS data recorded for the last session.";
        var parts = new List<string> { $"Avg {s.AverageFps:0} FPS", $"1% low {s.OnePercentLowFps:0}", $"0.1% low {s.PointOnePercentLowFps:0}" };
        if (s.TimeAtTarget is { } t) parts.Add($"{t:P0} of frames on target");
        if (s.StutterCount > 0) parts.Add($"{s.StutterCount} stutters");
        return string.Join("  ·  ", parts);
    }
}

public static class Sparkline
{
    /// <summary>Like <see cref="Build"/>, but scaled so the target fits too; returns the target's Y for the cap line.</summary>
    public static (PointCollection points, double? targetY) BuildWithTarget(IReadOnlyList<double> values, double width, double height, double? target)
    {
        var points = new PointCollection();
        if (values.Count < 2) { points.Freeze(); return (points, null); }
        var lo = Math.Min(values.Min(), target ?? double.MaxValue) * 0.92;
        var hi = Math.Max(values.Max(), target ?? 0) * 1.04;
        var range = Math.Max(hi - lo, 1);
        double Y(double v) => height - (v - lo) / range * height;
        for (var i = 0; i < values.Count; i++) points.Add(new Point(i * width / (values.Count - 1), Y(values[i])));
        points.Freeze();
        return (points, target is { } t ? Y(t) : null);
    }

    public static PointCollection Build(IReadOnlyList<double> values, double width, double height)
    {
        var points = new PointCollection();
        if (values.Count < 2) { points.Freeze(); return points; }
        var min = values.Min() * 0.95;
        var max = values.Max() * 1.02;
        var range = Math.Max(max - min, 1);
        for (var i = 0; i < values.Count; i++)
            points.Add(new Point(i * width / (values.Count - 1), height - (values[i] - min) / range * height));
        points.Freeze();
        return points;
    }
}
