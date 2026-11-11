using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Fps;

/// <summary>
/// Accumulates frame times for one session at constant memory cost: a 0.05 ms-resolution histogram
/// (5,000 ints) plus a 10-second FPS timeline. Percentiles (1% / 0.1% lows) are exact to 0.05 ms.
/// Thread-safe: frames are added from the ETW thread, snapshots read from the UI thread.
/// </summary>
public sealed class FrameTimeRecorder
{
    public const double BucketMs = 0.05;
    public const double MaxTrackedMs = 250;
    /// <summary>Gaps longer than this are loading screens / alt-tab, not gameplay, and are ignored.</summary>
    public const double IgnoreAboveMs = 1000;
    public const double TimelineBucketMs = 10_000;

    private readonly int[] _histogram = new int[(int)(MaxTrackedMs / BucketMs) + 1];
    private readonly List<float> _timeline = [];
    private readonly Lock _lock = new();
    private long _frames;
    private double _totalMs;
    private double _timelineMs;
    private int _timelineFrames;

    // Rolling window for the live "current FPS" readout.
    private readonly Queue<double> _recent = new();
    private double _recentMs;

    public long FrameCount { get { lock (_lock) return _frames; } }

    // Frames of the last 15 s with their times, for live decisions (auto TDP): "how is it running right now?"
    private readonly Queue<(long tick, float ms)> _window = new();
    private const double WindowSeconds = 15;

    /// <summary>Frames, average FPS and 1% low (99th percentile frame time) over roughly the last <paramref name="seconds"/> seconds.</summary>
    public (int Frames, double Fps, double LowFps) RecentStats(double seconds)
    {
        lock (_lock)
        {
            var cutoff = System.Diagnostics.Stopwatch.GetTimestamp() - (long)(seconds * System.Diagnostics.Stopwatch.Frequency);
            var times = new List<float>(_window.Count);
            foreach (var (tick, ms) in _window) if (tick >= cutoff) times.Add(ms);
            if (times.Count == 0) return (0, 0, 0);
            var total = 0.0;
            foreach (var t in times) total += t;
            times.Sort();
            var p99 = times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * 0.99) - 1)];
            return (times.Count, times.Count * 1000.0 / total, p99 > 0 ? 1000.0 / p99 : 0);
        }
    }

    // Timestamped spikes for the stutter detective (capped so memory stays constant).
    private readonly List<StutterEvent> _stutters = [];
    public const int MaxStutterEvents = 3000;

    /// <summary>Frames much slower than the ones around them, with when they happened.</summary>
    public List<StutterEvent> Stutters { get { lock (_lock) return [.. _stutters]; } }
    /// <summary>How many spikes so far (cheap; for live checks).</summary>
    public int StutterEventCount { get { lock (_lock) return _stutters.Count; } }

    public void AddFrame(double frameTimeMs)
    {
        if (frameTimeMs <= 0 || frameTimeMs > IgnoreAboveMs) return;
        lock (_lock)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            _window.Enqueue((now, (float)frameTimeMs));
            var drop = now - (long)(WindowSeconds * System.Diagnostics.Stopwatch.Frequency);
            while (_window.Count > 0 && _window.Peek().tick < drop) _window.Dequeue();

            _frames++;
            _totalMs += frameTimeMs;
            var idx = (int)Math.Min(frameTimeMs / BucketMs, _histogram.Length - 1);
            _histogram[idx]++;

            _timelineMs += frameTimeMs;
            _timelineFrames++;
            if (_timelineMs >= TimelineBucketMs)
            {
                _timeline.Add((float)(_timelineFrames * 1000.0 / _timelineMs));
                _timelineMs = 0;
                _timelineFrames = 0;
            }

            // A stutter: at least 25 ms and 2.5× the recent average frame time.
            if (frameTimeMs >= 25 && _recent.Count > 10 && frameTimeMs > 2.5 * (_recentMs / _recent.Count) && _stutters.Count < MaxStutterEvents)
                _stutters.Add(new StutterEvent(DateTimeOffset.UtcNow, (float)frameTimeMs));

            _recent.Enqueue(frameTimeMs);
            _recentMs += frameTimeMs;
            while (_recentMs > 1000 && _recent.Count > 1) _recentMs -= _recent.Dequeue();
        }
    }

    /// <summary>FPS over roughly the last second.</summary>
    public double CurrentFps
    {
        get { lock (_lock) return _recentMs <= 0 ? 0 : _recent.Count * 1000.0 / _recentMs; }
    }

    public double AverageFps
    {
        get { lock (_lock) return _totalMs <= 0 ? 0 : _frames * 1000.0 / _totalMs; }
    }

    /// <summary>Frame time (ms) at the given percentile (0-100), interpolated within the bucket.</summary>
    public double FrameTimePercentile(double percentile)
    {
        lock (_lock) return PercentileUnlocked(percentile);
    }

    private double PercentileUnlocked(double percentile)
    {
        if (_frames == 0) return 0;
        var rank = Math.Clamp(percentile / 100.0, 0, 1) * _frames;
        long cumulative = 0;
        for (var i = 0; i < _histogram.Length; i++)
        {
            if (_histogram[i] == 0) continue;
            if (cumulative + _histogram[i] >= rank)
            {
                var within = (rank - cumulative) / _histogram[i];
                return (i + Math.Clamp(within, 0, 1)) * BucketMs;
            }
            cumulative += _histogram[i];
        }
        return MaxTrackedMs;
    }

    public SessionSummary Summarise(Game game, DateTimeOffset start, DateTimeOffset end, int? targetFps)
    {
        lock (_lock)
        {
            var s = new SessionSummary
            {
                GameId = game.Id,
                GameName = game.Name,
                Start = start,
                End = end,
                TargetFps = targetFps,
                FrameCount = _frames,
                // Need at least ~10 seconds of frames for numbers to mean anything.
                HasFpsData = _frames >= 300,
            };
            if (!s.HasFpsData) return s;

            s.AverageFps = _frames * 1000.0 / _totalMs;
            var p99 = PercentileUnlocked(99);
            var p999 = PercentileUnlocked(99.9);
            var p50 = PercentileUnlocked(50);
            s.OnePercentLowFps = p99 > 0 ? 1000.0 / p99 : 0;
            s.PointOnePercentLowFps = p999 > 0 ? 1000.0 / p999 : 0;
            s.MedianFps = p50 > 0 ? 1000.0 / p50 : 0;
            s.StutterCount = CountAbove(p50 * 2.5);

            if (targetFps is > 0)
            {
                var budget = 1000.0 / targetFps.Value * 1.10;
                s.TimeAtTarget = 1.0 - (double)CountAbove(budget) / _frames;
            }

            s.FpsTimeline = [.. _timeline];
            if (_timelineFrames > 0 && _timelineMs > 2000) s.FpsTimeline.Add((float)(_timelineFrames * 1000.0 / _timelineMs));
            return s;
        }
    }

    private int CountAbove(double ms)
    {
        var start = (int)Math.Ceiling(ms / BucketMs);
        var count = 0;
        for (var i = Math.Max(start, 0); i < _histogram.Length; i++) count += _histogram[i];
        return count;
    }
}
