namespace FitzLauncher.Core.Models;

/// <summary>Compact record of one play session. Stored per session; no raw frame data is kept.</summary>
public sealed class SessionSummary
{
    public Guid GameId { get; set; }
    public string GameName { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public TimeSpan Duration => End - Start;
    public int? TargetFps { get; set; }

    /// <summary>False when no frames were captured (e.g. not running as admin, or an unsupported API).</summary>
    public bool HasFpsData { get; set; }
    public long FrameCount { get; set; }
    public double AverageFps { get; set; }
    /// <summary>"1% low": FPS at the 99th-percentile frame time.</summary>
    public double OnePercentLowFps { get; set; }
    public double PointOnePercentLowFps { get; set; }
    public double MedianFps { get; set; }
    /// <summary>Share of frames (0-1) delivered at or faster than the target frame time (+10% tolerance).</summary>
    public double? TimeAtTarget { get; set; }
    /// <summary>Count of frames that took more than 2.5x the median frame time (visible stutter).</summary>
    public int StutterCount { get; set; }

    public double? MaxCpuTempC { get; set; }
    public double? MaxGpuTempC { get; set; }
    public double? AvgCpuTempC { get; set; }
    public double? AvgGpuTempC { get; set; }
    public int TempAlerts { get; set; }

    /// <summary>Average FPS per 10-second bucket, for the small timeline chart.</summary>
    public List<float> FpsTimeline { get; set; } = [];
}
