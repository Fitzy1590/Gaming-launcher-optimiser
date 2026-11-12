using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.Core.Fps;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.App.ViewModels;

public sealed partial class StutterCauseRow(StutterCauseFinding f, GameDetailsViewModel owner) : ObservableObject
{
    public string Title => $"{f.Title} · {f.Count} stutter(s)";
    public string Detail => f.Detail;
    public string Fix => f.Fix;
    public bool HasAction => f.Action != StutterFix.None;
    public string ActionLabel => f.ActionLabel;
    [ObservableProperty] private string _result = "";

    [RelayCommand]
    private void Apply() => Result = owner.ApplyStutterFix(f.Action) ?? "Already set: nothing more to change here.";
}

public sealed partial class AbOption(object? value, string label, bool isB, GameDetailsViewModel owner) : ObservableObject
{
    public object? Value { get; } = value;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
    public IRelayCommand PickCommand { get; } = new RelayCommand(() => owner.PickAb(isB, value, label));
}

/// <summary>Game page: the stutter detective's report and the A/B benchmark wizard.</summary>
public sealed partial class GameDetailsViewModel
{
    // ------------------------------------------------------------------ stutter detective

    public ObservableCollection<StutterCauseRow> StutterCauses { get; } = [];
    public ObservableCollection<string> StutterMoments { get; } = [];
    [ObservableProperty] private bool _hasStutterReport;
    [ObservableProperty] private string _stutterSummary = "";

    private void LoadStutters()
    {
        var r = Json.Load<StutterReport>(Path.Combine(_s.Paths.Root, "stutters", $"{Game.Id:N}.json"));
        HasStutterReport = r is not null;
        StutterCauses.Clear();
        StutterMoments.Clear();
        if (r is null) return;
        StutterSummary = $"{r.SessionStart.ToLocalTime():d MMM HH:mm}: {r.Summary}";
        foreach (var c in r.Causes.Take(4)) StutterCauses.Add(new StutterCauseRow(c, this));
        foreach (var m in r.WorstMoments)
            StutterMoments.Add($"{(int)m.At.TotalMinutes:00}:{m.At.Seconds:00}  ·  {m.WorstMs:0} ms{(m.Frames > 1 ? $" ({m.Frames} spikes)" : "")}  ·  {m.Cause switch { StutterCause.ShaderCompile => "shader compiling", StutterCause.BackgroundApp => "background app", StutterCause.Memory => "memory", StutterCause.Thermal => "heat", StutterCause.CpuLimit => "CPU maxed", StutterCause.DiskBusy => "busy drive", StutterCause.PowerChange => "power limit change", _ => "frame pacing" }}{(m.Evidence.Length > 0 ? $": {m.Evidence}" : "")}");
    }

    /// <summary>Applies one of the detective's suggested fixes to this game (or the launcher setting it names).</summary>
    public string? ApplyStutterFix(StutterFix fix)
    {
        var done = StutterFixes.Apply(Game, _s.Settings, fix);
        if (done is not null) { _s.SaveLibrary(); _s.SaveSettings(); }
        return done;
    }

    // ------------------------------------------------------------------ A/B wizard

    public ObservableCollection<AbOption> AbOptionsA { get; } = [];
    public ObservableCollection<AbOption> AbOptionsB { get; } = [];
    [ObservableProperty] private AbKind _abKind = AbKind.Cooling;
    [ObservableProperty] private string _abManualA = "";
    [ObservableProperty] private string _abManualB = "";
    private AbSetup _pendingA = new(), _pendingB = new();

    public bool AbIdle => Game.AbTest is null;
    public bool AbRunning => Game.AbTest is { Done: false };
    public bool AbDone => Game.AbTest is { Done: true };
    public bool AbIsManual => AbKind == AbKind.Manual;
    public string AbEndText => AbDone ? "New test" : "Cancel test";
    public string AbStatus => Game.AbTest switch
    {
        { Done: true } t => t.Verdict(),
        { } t => $"Next: press Play for run {t.NextName} ({t.Next!.Label}). Play the same part each time; recording starts after {t.WarmupSeconds} s and lasts {t.Seconds} s." +
                 (t.Kind == AbKind.Manual ? $" Before run {t.NextName}, set up: {t.Next.Label}." : ""),
        _ => "",
    };

    partial void OnAbKindChanged(AbKind value) => BuildAbOptions();

    private void BuildAbOptions()
    {
        AbOptionsA.Clear();
        AbOptionsB.Clear();
        IEnumerable<(object v, string l)> opts = AbKind switch
        {
            AbKind.Cooling => Enum.GetValues<CoolingMode>().Select(m => ((object)m, m.ToString())),
            AbKind.FpsCap => new[] { 0, 60 }.Concat(Core.Models.LauncherSettings.FpsTargets).Select(c => ((object)c, c == 0 ? "No cap" : $"{c} FPS")),
            _ => [],
        };
        foreach (var (v, l) in opts)
        {
            AbOptionsA.Add(new AbOption(v, l, false, this));
            AbOptionsB.Add(new AbOption(v, l, true, this));
        }
        _pendingA = new AbSetup();
        _pendingB = new AbSetup();
        OnPropertyChanged(nameof(AbIsManual));
    }

    public void PickAb(bool isB, object? value, string label)
    {
        var setup = new AbSetup { Label = label, Cooling = value as CoolingMode?, Cap = value as int? };
        if (isB) _pendingB = setup; else _pendingA = setup;
        foreach (var o in isB ? AbOptionsB : AbOptionsA) o.IsSelected = o.Label == label;
    }

    [RelayCommand] private void SetAbKind(string kind) => AbKind = Enum.Parse<AbKind>(kind);

    [RelayCommand]
    private void StartAb()
    {
        if (AbKind == AbKind.Manual) { _pendingA = new AbSetup { Label = AbManualA.Trim() }; _pendingB = new AbSetup { Label = AbManualB.Trim() }; }
        if (_pendingA.Label.Length == 0 || _pendingB.Label.Length == 0 || _pendingA.Label == _pendingB.Label)
        {
            Status = "Pick two different setups for A and B.";
            return;
        }
        Game.AbTest = new AbTest { Kind = AbKind, A = _pendingA, B = _pendingB };
        _s.SaveLibrary();
        RefreshAb();
        Status = "A/B test ready. Press Play for run A.";
    }

    [RelayCommand]
    private void CancelAb()
    {
        Game.AbTest = null;
        _s.SaveLibrary();
        RefreshAb();
    }

    /// <summary>Keeps the winning setup (cooling mode or FPS cap).</summary>
    [RelayCommand]
    private async Task ApplyAbWinnerAsync()
    {
        if (Game.AbTest is not { Done: true } t) return;
        var verdict = t.Verdict();
        if (verdict.StartsWith("Too close", StringComparison.Ordinal)) { Status = "No clear winner: keep whichever you prefer."; return; }
        var winner = verdict.StartsWith("A ", StringComparison.Ordinal) ? t.A : t.B;
        if (winner.Cooling is { } c) { Game.Cooling = c; _s.SaveLibrary(); Status = $"Cooling set to {c} for this game."; }
        else if (winner.Cap is { } cap) await OptimiseAtAsync(cap == 0 ? null : cap);
        else Status = $"Keep the setup you used for {winner.Label}.";
        LoadCooling();
    }

    private void RefreshAb()
    {
        foreach (var p in new[] { nameof(AbIdle), nameof(AbRunning), nameof(AbDone), nameof(AbStatus), nameof(AbIsManual), nameof(AbEndText) }) OnPropertyChanged(p);
    }

    private void LoadInsights()
    {
        LoadStutters();
        RefreshSmooth();
        if (AbOptionsA.Count == 0) BuildAbOptions();
        RefreshAb();
    }
}
