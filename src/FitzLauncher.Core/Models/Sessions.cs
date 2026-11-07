namespace FitzLauncher.Core.Models;

/// <summary>Compact record of one play session. Stored per session; no raw frame data is kept.</summary>
public sealed class SessionSummary
{
    public Guid GameId { get; set; }
    public string GameName { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    /// <summary>Time the device was asleep with the game paused; not counted as play time.</summary>
    public double AsleepSeconds { get; set; }
    public TimeSpan Duration => End - Start - TimeSpan.FromSeconds(AsleepSeconds);
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
    public double? AvgGpuLoad { get; set; }
    public bool Crashed { get; set; }
    public string? FaultingModule { get; set; }
    /// <summary>Package power limit used (watts), when the launcher set one.</summary>
    public int? PowerWatts { get; set; }
    /// <summary>RTSS limiter mode used (0 async, 1 front-edge sync), when the launcher set one.</summary>
    public int? LimiterMode { get; set; }

    // Display the session ran on, for per-display learning.
    public string? DisplayKey { get; set; }
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }
    public int RenderScale { get; set; } = 100;

    /// <summary>Average FPS per 10-second bucket, for the small timeline chart.</summary>
    public List<float> FpsTimeline { get; set; } = [];
}

/// <summary>A fixed-length capture started with the benchmark hotkey, for fair before/after comparisons.</summary>
public sealed class BenchmarkResult
{
    public Guid GameId { get; set; }
    public string GameName { get; set; } = "";
    public DateTimeOffset Taken { get; set; } = DateTimeOffset.Now;
    public int Seconds { get; set; }
    public string Label { get; set; } = "";
    public int? TargetFps { get; set; }
    public double AverageFps { get; set; }
    public double OnePercentLowFps { get; set; }
    public double PointOnePercentLowFps { get; set; }
    public double? TimeAtTarget { get; set; }
    public int StutterCount { get; set; }
    public double? MaxGpuTempC { get; set; }
    /// <summary>Tweaks that were active, so runs can be compared against what changed.</summary>
    public List<string> ActiveTweaks { get; set; } = [];

    public static BenchmarkResult From(SessionSummary s, string label, IEnumerable<string> tweaks) => new()
    {
        GameId = s.GameId, GameName = s.GameName, Taken = s.End, Seconds = (int)s.Duration.TotalSeconds, Label = label,
        TargetFps = s.TargetFps, AverageFps = s.AverageFps, OnePercentLowFps = s.OnePercentLowFps,
        PointOnePercentLowFps = s.PointOnePercentLowFps, TimeAtTarget = s.TimeAtTarget, StutterCount = s.StutterCount,
        MaxGpuTempC = s.MaxGpuTempC, ActiveTweaks = tweaks.Distinct().ToList(),
    };
}
