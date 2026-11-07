using FitzLauncher.Core.Fps;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Tests;

public class FrameTimeRecorderTests
{
    private static readonly Game G = new() { Name = "Test" };

    [Fact]
    public void Steady_10ms_frames_are_100_fps_everywhere()
    {
        var r = new FrameTimeRecorder();
        for (var i = 0; i < 6000; i++) r.AddFrame(10.0);

        var s = r.Summarise(G, DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now, 100);
        Assert.True(s.HasFpsData);
        Assert.Equal(100, s.AverageFps, 1);
        Assert.InRange(s.OnePercentLowFps, 99, 101);
        Assert.InRange(s.PointOnePercentLowFps, 99, 101);
        Assert.Equal(0, s.StutterCount);
        Assert.Equal(1.0, s.TimeAtTarget!.Value, 3);
        Assert.Equal(6, s.FpsTimeline.Count); // 60 s / 10 s buckets
        Assert.All(s.FpsTimeline, f => Assert.Equal(100f, f, 1f));
    }

    [Fact]
    public void Spikes_drag_the_one_percent_low_but_not_the_median()
    {
        var r = new FrameTimeRecorder();
        // 98% of frames at 8.33 ms (120 fps), 2% at 25 ms (40 fps).
        for (var i = 0; i < 10_000; i++) r.AddFrame(i % 50 == 0 ? 25.0 : 8.33);

        var s = r.Summarise(G, DateTimeOffset.Now, DateTimeOffset.Now, 117);
        Assert.InRange(s.MedianFps, 119, 121);
        Assert.InRange(s.OnePercentLowFps, 39, 41);
        Assert.Equal(200, s.StutterCount);
        Assert.InRange(s.TimeAtTarget!.Value, 0.979, 0.981);
        // 10,000 frames over 9,800×8.33 + 200×25 ms = 86.6 s → 115.4 fps
        Assert.InRange(s.AverageFps, 115, 116);
    }

    [Fact]
    public void Loading_screen_gaps_are_ignored()
    {
        var r = new FrameTimeRecorder();
        for (var i = 0; i < 1000; i++) r.AddFrame(10);
        r.AddFrame(5000); // 5 s loading screen
        Assert.Equal(1000, r.FrameCount);
        Assert.Equal(100, r.AverageFps, 1);
    }

    [Fact]
    public void Too_few_frames_reports_no_data()
    {
        var r = new FrameTimeRecorder();
        for (var i = 0; i < 50; i++) r.AddFrame(10);
        Assert.False(r.Summarise(G, DateTimeOffset.Now, DateTimeOffset.Now, null).HasFpsData);
    }

    [Fact]
    public void Current_fps_uses_roughly_the_last_second()
    {
        var r = new FrameTimeRecorder();
        for (var i = 0; i < 500; i++) r.AddFrame(20); // 50 fps
        for (var i = 0; i < 200; i++) r.AddFrame(5);  // 200 fps for 1 s
        Assert.InRange(r.CurrentFps, 190, 201);
    }
}
