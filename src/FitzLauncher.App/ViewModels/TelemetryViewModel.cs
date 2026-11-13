using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FitzLauncher.App.Services;
using FitzLauncher.Core.Telemetry;

namespace FitzLauncher.App.ViewModels;

/// <summary>One of the four rules on the Telemetry page.</summary>
public sealed partial class TunerRuleRow(TunerRule rule, string name, string what) : ObservableObject
{
    public TunerRule Rule { get; } = rule;
    public string Name { get; } = name;
    public string What { get; } = what;
    [ObservableProperty] private string _state = "Waiting";
    [ObservableProperty] private string _count = "";
    [ObservableProperty] private bool _canRevert;
}

public sealed record TelemetryLogRow(string Time, string Rule, string Text, string Outcome);

/// <summary>
/// The Telemetry page (SPEC §12.3): live tiles, a 60 s graph, the four rules, the action log and the mode switch.
/// It only shows what <see cref="FitzLauncher.Windows.ClosedLoopSession"/> publishes once a second; it decides nothing.
/// </summary>
public sealed partial class TelemetryViewModel : ObservableObject
{
    private const double GraphWidth = 300, GraphHeight = 80;
    private readonly AppServices _s;
    private readonly Func<TunerRule, Task> _revert;

    public TelemetryViewModel(AppServices services, Func<TunerRule, Task> revert)
    {
        _s = services;
        _revert = revert;
        _mode = services.Settings.ClosedLoop;
        Rules =
        [
            new(TunerRule.CpuBound, "Processor-bound", "The processor is the limit. Gives the game higher priority and the fast cores, and holds back background apps."),
            new(TunerRule.GpuBound, "Graphics-bound", "The graphics chip is the limit. Raises the power profile (advice only while Auto TDP is on)."),
            new(TunerRule.MicroStutter, "Stutter", "A few slow frames stand out. Caps the frame rate just under the screen's refresh rate and trims spare memory."),
            new(TunerRule.Thermal, "Too hot", "Over 85 °C. Goes back to the standard power profile and shows a warning. Beats every other rule."),
        ];
        Apply(ClosedLoopSnapshot.Idle with { Mode = _mode });
    }

    public ObservableCollection<TunerRuleRow> Rules { get; }
    public ObservableCollection<TelemetryLogRow> Log { get; } = [];

    /// <summary>Off, Watch only or On. Saved straight away and used from the next game you start.</summary>
    [ObservableProperty] private ClosedLoopMode _mode;

    partial void OnModeChanged(ClosedLoopMode value)
    {
        if (_s.Settings.ClosedLoop == value) return;
        _s.Settings.ClosedLoop = value;
        _s.SaveSettings();
        OnPropertyChanged(nameof(ModeNote));
    }

    public string ModeNote => Mode switch
    {
        ClosedLoopMode.WatchOnly => "Watch only: it decides and writes to the log, but changes nothing. Applies from the next game you start. A good first try.",
        ClosedLoopMode.On => "On: it changes priority, cores, power profile and the frame cap when a rule fires, and puts everything back when the game ends. Applies from the next game you start.",
        _ => "Off: nothing is measured or changed. Applies from the next game you start.",
    };

    [ObservableProperty] private string _fpsText = "–";
    [ObservableProperty] private string _lowText = "–";
    [ObservableProperty] private string _cpuBusyText = "–";
    [ObservableProperty] private string _gpuBusyText = "–";
    [ObservableProperty] private string _cpuTempText = "–";
    [ObservableProperty] private string _gpuTempText = "–";
    [ObservableProperty] private PointCollection _graph = [];
    [ObservableProperty] private string? _whyNotRunning;

    public bool HasLog => Log.Count > 0;
    public bool HasWhyNot => WhyNotRunning is not null;
    partial void OnWhyNotRunningChanged(string? value) => OnPropertyChanged(nameof(HasWhyNot));

    /// <summary>Takes a new reading. Call on the UI thread.</summary>
    public void Apply(ClosedLoopSnapshot s)
    {
        FpsText = s.Fps > 0 ? s.Fps.ToString("0", CultureInfo.CurrentCulture) : "–";
        LowText = s.LowFps > 0 ? s.LowFps.ToString("0", CultureInfo.CurrentCulture) : "–";
        CpuBusyText = Ms(s.CpuBusyMs);
        GpuBusyText = Ms(s.GpuBusyMs);
        CpuTempText = Deg(s.CpuTempC);
        GpuTempText = Deg(s.GpuTempC);
        WhyNotRunning = s.WhyNotRunning;
        Graph = BuildGraph(s.FrameFps);

        foreach (var st in s.Rules)
        {
            if (Rules.FirstOrDefault(r => r.Rule == st.Rule) is not { } row) continue;
            row.State = st.Active ? "Active"
                : st.GivenUp ? "Stopped for this session"
                : st.CooldownLeft > TimeSpan.Zero ? $"Cooling down, {Math.Ceiling(st.CooldownLeft.TotalSeconds):0} s"
                : "Waiting";
            row.Count = st.Applications == 0 ? "" : $"Acted {st.Applications} of {ClosedLoopTuner.MaxApplicationsPerRule} times";
            row.CanRevert = st.Active && s.Mode == ClosedLoopMode.On;
        }

        // The session keeps at most 200 lines, so once full the count stops changing: compare the newest line too.
        if (Log.Count != s.Log.Count || (s.Log.Count > 0 && Log[0].Text != s.Log[^1].Text) || (s.Log.Count > 0 && Log[0].Time != s.Log[^1].At.ToString("HH:mm:ss", CultureInfo.CurrentCulture)))
        {
            Log.Clear();
            foreach (var e in s.Log.Reverse()) // newest first
                Log.Add(new TelemetryLogRow(e.At.ToString("HH:mm:ss", CultureInfo.CurrentCulture), RuleName(e.Rule), e.Text, Outcome(e)));
            OnPropertyChanged(nameof(HasLog));
        }
    }

    [RelayCommand] private Task RevertAsync(TunerRule rule) => _revert(rule);

    private static string Ms(double? v) => v is { } x ? x.ToString("0.0", CultureInfo.CurrentCulture) + " ms" : "–";
    private static string Deg(double? v) => v is { } x ? x.ToString("0", CultureInfo.CurrentCulture) + " °C" : "–";

    private static string RuleName(TunerRule? r) => r switch
    {
        TunerRule.CpuBound => "Processor-bound",
        TunerRule.GpuBound => "Graphics-bound",
        TunerRule.MicroStutter => "Stutter",
        TunerRule.Thermal => "Too hot",
        _ => "Note",
    };

    private static string Outcome(ClosedLoopLogEntry e) => e.Type switch
    {
        TunerActionType.Note => "Note",
        TunerActionType.Release or TunerActionType.Undo => e.Applied ? "Put back" : "Would put back",
        _ => e.Applied ? "Done" : "Not changed",
    };

    private static PointCollection BuildGraph(IReadOnlyList<float> fps)
    {
        var points = new PointCollection();
        if (fps.Count < 2) return points;
        var max = Math.Max(1, fps.Max());
        for (var i = 0; i < fps.Count; i++)
            points.Add(new System.Windows.Point(i * GraphWidth / (fps.Count - 1), GraphHeight - fps[i] / max * GraphHeight));
        return points;
    }
}
