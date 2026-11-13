using FitzLauncher.Core.Telemetry;

namespace FitzLauncher.Core.Tests;

public class RollingFrameStatsTests
{
    /// <summary>Feeds steady frames of <paramref name="frameMs"/> from <paramref name="fromMs"/> for <paramref name="seconds"/>; returns the end time.</summary>
    private static double Feed(RollingFrameStats s, double fromMs, double seconds, double frameMs, double? cpu = null, double? gpu = null)
    {
        var t = fromMs;
        var end = fromMs + seconds * 1000;
        while (t < end) { t += frameMs; s.Add(t, frameMs, cpu, gpu); }
        return t;
    }

    [Fact]
    public void First_five_seconds_are_ignored()
    {
        var s = new RollingFrameStats();
        var end = Feed(s, 0, 5, 10);
        Assert.False(s.Snapshot(end).HasData);
        end = Feed(s, end, 2, 10);
        Assert.True(s.Snapshot(end).HasData);
    }

    [Fact]
    public void Steady_10ms_frames_are_100_fps_with_a_matching_1_percent_low()
    {
        var s = new RollingFrameStats();
        var end = Feed(s, 0, 20, 10);
        var w = s.Snapshot(end).Window;
        Assert.Equal(100, w.AvgFps, 1);
        Assert.InRange(w.OnePercentLowFps, 99, 101);
        Assert.Equal(0, w.Stalls);
    }

    [Fact]
    public void Window_is_time_based_not_frame_count()
    {
        // 30 FPS for 30 s, then 240 FPS for 60 s: the slow part must have left the window entirely.
        var s = new RollingFrameStats();
        var t = Feed(s, 0, 36, 1000.0 / 30);
        t = Feed(s, t, 60.5, 1000.0 / 240);
        var snap = s.Snapshot(t);
        Assert.Equal(240, snap.Window.AvgFps, 0);
        Assert.InRange(snap.Window.OnePercentLowFps, 235, 245);
        Assert.InRange(snap.Buckets.Count, 59, 61);
    }

    [Fact]
    public void Old_frames_drop_out_as_time_passes_with_no_new_frames()
    {
        var s = new RollingFrameStats();
        var end = Feed(s, 0, 20, 10);
        Assert.True(s.Snapshot(end).HasData);
        Assert.False(s.Snapshot(end + 61_000).HasData);
    }

    [Fact]
    public void Stalls_over_one_second_are_counted_and_left_out_of_the_averages()
    {
        var s = new RollingFrameStats();
        var t = Feed(s, 0, 10, 10);
        t += 1500; s.Add(t, 1500);
        t = Feed(s, t, 2, 10);
        var w = s.Snapshot(t).Window;
        Assert.Equal(1, w.Stalls);
        Assert.Equal(100, w.AvgFps, 1);
        Assert.InRange(w.OnePercentLowFps, 99, 101);
    }

    [Fact]
    public void One_percent_low_catches_a_few_slow_frames()
    {
        var s = new RollingFrameStats();
        var t = 0.0;
        for (var i = 0; i < 3000; i++)
        {
            var ms = i % 50 == 0 ? 50.0 : 10.0; // 2 % of frames are 50 ms
            t += ms; s.Add(t, ms);
        }
        var w = s.Snapshot(t).Window;
        Assert.InRange(w.OnePercentLowFps, 19, 21);
        Assert.InRange(w.MinFps, 19, 21);
        Assert.True(w.AvgFps > 70);
    }

    [Fact]
    public void Fps_above_the_cap_is_clamped_to_the_cap()
    {
        var s = new RollingFrameStats(fpsCap: 60);
        var end = Feed(s, 0, 20, 5); // runs at 200 FPS uncapped
        var w = s.Snapshot(end).Window;
        Assert.Equal(60, w.AvgFps, 1);
    }

    [Fact]
    public void Buckets_are_one_second_wide_and_ordered()
    {
        var s = new RollingFrameStats();
        var end = Feed(s, 0, 15, 10);
        var b = s.Snapshot(end).Buckets;
        Assert.InRange(b.Count, 10, 11);
        for (var i = 1; i < b.Count; i++) Assert.Equal(1000, b[i].StartMs - b[i - 1].StartMs, 3);
        Assert.All(b.Skip(1).Take(b.Count - 2), x => Assert.InRange(x.AvgFps, 99, 101));
    }

    [Fact]
    public void Busy_medians_are_reported_only_when_frames_carry_them()
    {
        var none = new RollingFrameStats();
        var e1 = Feed(none, 0, 10, 10);
        Assert.Null(none.Snapshot(e1).Window.MedianCpuBusyMs);
        Assert.Null(none.Snapshot(e1).Window.MedianGpuBusyMs);

        var with = new RollingFrameStats();
        var e2 = Feed(with, 0, 10, 10, cpu: 8, gpu: 5);
        var w = with.Snapshot(e2).Window;
        Assert.Equal(8, w.MedianCpuBusyMs!.Value, 3);
        Assert.Equal(5, w.MedianGpuBusyMs!.Value, 3);
    }

    [Fact]
    public void Empty_and_bad_input_never_throws()
    {
        var s = new RollingFrameStats();
        s.Add(100, 0);
        s.Add(100, -5);
        s.Add(double.NaN, 10);
        s.Add(100, double.NaN);
        Assert.False(s.Snapshot(1000).HasData);
    }
}
