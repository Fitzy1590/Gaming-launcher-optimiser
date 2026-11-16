namespace FitzLauncher.Core.Telemetry;

/// <summary>
/// Stand-in for per-frame CPU/GPU busy times until the ETW readings are verified (SPEC §12, §14).
/// If the game's graphics engine is well under full while the game runs below its pace, the graphics
/// chip is waiting, so the processor side is the limit. The estimate treats the whole frame as CPU time
/// and the share the GPU engine was busy as GPU time, which makes CPU/GPU read as 1 / GPU load.
/// Returns null (can't tell) when the game is at its cap or the screen's refresh rate, because a low
/// GPU load there only means the game is finished early.
/// </summary>
public static class BusyEstimator
{
    /// <summary>Within this share of the cap or refresh rate counts as "at pace".</summary>
    public const double AtPaceShare = 0.95;
    /// <summary>Below this GPU load the reading is too small to divide by.</summary>
    public const double MinGpuPercent = 5;

    public static (double CpuMs, double GpuMs)? Estimate(double avgFps, double? gpuPercent, int capFps, int monitorHz)
    {
        if (avgFps <= 0 || gpuPercent is not { } g || g < MinGpuPercent) return null;
        var limit = new[] { capFps, monitorHz }.Where(v => v > 0).DefaultIfEmpty(0).Min();
        if (limit > 0 && avgFps >= limit * AtPaceShare) return null;
        var frameMs = 1000.0 / avgFps;
        return (frameMs, frameMs * Math.Min(g, 100) / 100.0);
    }
}
