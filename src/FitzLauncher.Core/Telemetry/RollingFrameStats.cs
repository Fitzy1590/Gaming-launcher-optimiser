namespace FitzLauncher.Core.Telemetry;

/// <summary>One second of play: how many frames, how fast, how smooth.</summary>
/// <param name="StartMs">When the second began (same clock as the frames).</param>
/// <param name="Frames">Frames counted (stalls left out).</param>
/// <param name="Stalls">Frames that took over a second.</param>
/// <param name="AvgFps">Average FPS over the second.</param>
/// <param name="OnePercentLowFps">1000 / the 99th-percentile frame time.</param>
/// <param name="MinFps">Slowest frame, as FPS.</param>
/// <param name="MedianCpuBusyMs">Median CPU busy time per frame, or null when the frames carried none.</param>
/// <param name="MedianGpuBusyMs">Median GPU busy time per frame, or null when the frames carried none.</param>
public readonly record struct FrameBucket(
    double StartMs, int Frames, int Stalls, double AvgFps, double OnePercentLowFps, double MinFps,
    double? MedianCpuBusyMs, double? MedianGpuBusyMs);

/// <summary>What the last minute looked like: the whole window plus its one-second buckets, oldest first.</summary>
public sealed record FrameStatsSnapshot(FrameBucket Window, IReadOnlyList<FrameBucket> Buckets)
{
    public static readonly FrameStatsSnapshot Empty = new(default, []);
    public bool HasData => Window.Frames > 0;
}

/// <summary>
/// Rolling frame statistics for the closed-loop tuner (SPEC §12.1). Time-based, never a frame count:
/// a 60 s window cut by timestamp, so a 30 FPS game and a 240 FPS game are judged over the same minute.
/// <list type="bullet">
/// <item>1% low = 1000 / p99 frame time over the window.</item>
/// <item>Frames over 1 s are stalls: counted, left out of averages and percentiles.</item>
/// <item>The first 5 s after the first frame are ignored (shader compiles, loading).</item>
/// <item>With a cap set, frames faster than the cap are counted as the cap (FPS above it is clamped, as Auto TDP does).</item>
/// </list>
/// Pure and clock-free: the caller supplies timestamps. Thread-safe (ETW thread adds, sampler thread reads).
/// </summary>
public sealed class RollingFrameStats
{
    public const double WindowMs = 60_000;
    public const double BucketMs = 1_000;
    public const double StallMs = 1_000;
    public const double WarmUpMs = 5_000;

    private readonly record struct Frame(double At, double Ms, double? Cpu, double? Gpu);

    private readonly Queue<Frame> _frames = new();
    private readonly Lock _lock = new();
    private double? _origin;
    private double _minFrameMs;

    /// <param name="fpsCap">The frame cap in force (null or 0 for none).</param>
    public RollingFrameStats(double? fpsCap = null) => SetCap(fpsCap);

    /// <summary>Changes the cap used for clamping frames added from now on.</summary>
    public void SetCap(double? fpsCap)
    {
        lock (_lock) _minFrameMs = fpsCap is > 0 ? 1000.0 / fpsCap.Value : 0;
    }

    /// <param name="timestampMs">When the frame was presented, on any steady clock.</param>
    /// <param name="frameMs">Time since the previous frame.</param>
    public void Add(double timestampMs, double frameMs, double? cpuBusyMs = null, double? gpuBusyMs = null)
    {
        if (frameMs <= 0 || double.IsNaN(frameMs) || double.IsNaN(timestampMs)) return;
        lock (_lock)
        {
            _origin ??= timestampMs;
            if (timestampMs - _origin.Value < WarmUpMs) return;
            var ms = frameMs > StallMs ? frameMs : Math.Max(frameMs, _minFrameMs);
            _frames.Enqueue(new Frame(timestampMs, ms, cpuBusyMs, gpuBusyMs));
            Trim(timestampMs);
        }
    }

    /// <summary>Stats over the window ending at <paramref name="nowMs"/>.</summary>
    public FrameStatsSnapshot Snapshot(double nowMs)
    {
        lock (_lock)
        {
            Trim(nowMs);
            if (_frames.Count == 0) return FrameStatsSnapshot.Empty;

            var buckets = new List<FrameBucket>();
            var all = new List<Frame>(_frames);
            var i = 0;
            while (i < all.Count)
            {
                var start = Math.Floor(all[i].At / BucketMs) * BucketMs;
                var j = i;
                while (j < all.Count && all[j].At < start + BucketMs) j++;
                buckets.Add(Summarise(start, all.GetRange(i, j - i)));
                i = j;
            }
            return new FrameStatsSnapshot(Summarise(all[0].At, all), buckets);
        }
    }

    private void Trim(double nowMs)
    {
        var cutoff = nowMs - WindowMs;
        while (_frames.Count > 0 && _frames.Peek().At <= cutoff) _frames.Dequeue();
    }

    private static FrameBucket Summarise(double startMs, List<Frame> frames)
    {
        var good = new List<double>(frames.Count);
        var stalls = 0;
        var total = 0.0;
        foreach (var f in frames)
        {
            if (f.Ms > StallMs) { stalls++; continue; }
            good.Add(f.Ms);
            total += f.Ms;
        }
        if (good.Count == 0) return new FrameBucket(startMs, 0, stalls, 0, 0, 0, null, null);

        good.Sort();
        var p99 = good[Math.Min(good.Count - 1, (int)Math.Ceiling(good.Count * 0.99) - 1)];
        return new FrameBucket(startMs, good.Count, stalls, good.Count * 1000.0 / total, 1000.0 / p99, 1000.0 / good[^1],
            Median(frames.Where(f => f.Ms <= StallMs && f.Cpu.HasValue).Select(f => f.Cpu!.Value)),
            Median(frames.Where(f => f.Ms <= StallMs && f.Gpu.HasValue).Select(f => f.Gpu!.Value)));
    }

    private static double? Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        if (v.Count == 0) return null;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
