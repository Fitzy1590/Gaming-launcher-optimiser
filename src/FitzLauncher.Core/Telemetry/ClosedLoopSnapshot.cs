namespace FitzLauncher.Core.Telemetry;

/// <summary>One line of the Telemetry page's action log. <paramref name="Applied"/> is false in Watch only: it would have, but didn't.</summary>
public sealed record ClosedLoopLogEntry(DateTimeOffset At, TunerRule? Rule, TunerActionType Type, string Text, bool Applied);

/// <summary>Everything the Telemetry page shows, rebuilt once a second.</summary>
public sealed record ClosedLoopSnapshot(
    ClosedLoopMode Mode,
    bool GameRunning,
    double Fps,
    double LowFps,
    double? CpuBusyMs,
    double? GpuBusyMs,
    double? CpuTempC,
    double? GpuTempC,
    IReadOnlyList<float> FrameFps,
    IReadOnlyList<TunerRuleStatus> Rules,
    string? WhyNotRunning,
    IReadOnlyList<ClosedLoopLogEntry> Log)
{
    public static readonly ClosedLoopSnapshot Idle = new(ClosedLoopMode.Off, false, 0, 0, null, null, null, null, [], [],
        ClosedLoopAdvice.WhyNotRunning(ClosedLoopMode.Off, false, null, false, false), []);
}
