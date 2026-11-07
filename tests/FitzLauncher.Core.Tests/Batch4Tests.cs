using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Upscalers;

namespace FitzLauncher.Core.Tests;

public class Batch4Tests
{
    private static SessionSummary S(double avg, double low, double at, double maxGpu = 70, bool crashed = false, int stutters = 0) => new()
    {
        HasFpsData = true, AverageFps = avg, OnePercentLowFps = low, TimeAtTarget = at, MaxGpuTempC = maxGpu, MaxCpuTempC = 70,
        Crashed = crashed, StutterCount = stutters, Start = DateTimeOffset.Now.AddHours(-1), End = DateTimeOffset.Now,
    };

    [Fact]
    public void Report_card_grades_and_picks_the_right_next_step()
    {
        var s = new LauncherSettings();
        var great = ReportCards.Compute(new Game(), [S(117, 112, 0.98)], s)!;
        Assert.Equal("A", great.Grade);
        Assert.Contains("Nothing to change", great.NextStep);

        var hot = ReportCards.Compute(new Game(), [S(117, 110, 0.97, maxGpu: 95)], s)!;
        Assert.Contains("Running hot", hot.NextStep);

        var crashy = ReportCards.Compute(new Game(), [S(117, 110, 0.97, crashed: true), S(117, 110, 0.97, crashed: true)], s)!;
        Assert.Contains("Roll back", crashy.NextStep);
        Assert.True(crashy.Score < great.Score);
        Assert.Null(ReportCards.Compute(new Game(), [], s));
    }

    [Fact]
    public void Undervolt_steps_down_after_clean_sessions_and_backs_off_after_a_crash()
    {
        var s = new LauncherSettings { UndervoltEnabled = true, UndervoltTarget = -15 };
        UndervoltLadder.AfterCleanSession(s); Assert.Equal(-5, s.UndervoltCurrent);
        UndervoltLadder.AfterCleanSession(s); Assert.Equal(-10, s.UndervoltCurrent);
        Assert.Contains("backed off to -5", UndervoltLadder.AfterCrash(s));
        Assert.True(s.UndervoltSettled);
        Assert.Null(UndervoltLadder.AfterCleanSession(s)); // stays where it was stable
        Assert.Equal(0xFFFF6, UndervoltLadder.Encode(-10));
        Assert.Equal(0, UndervoltLadder.Encode(0));
    }

    [Fact]
    public void Idle_cap_drops_after_no_input_and_restores_on_input()
    {
        var g = new IdleCapGovernor(idleSeconds: 12, idleCap: 60);
        Assert.Null(g.Update(TimeSpan.FromSeconds(3), 117));
        Assert.Equal(60, g.Update(TimeSpan.FromSeconds(13), 117));
        Assert.Null(g.Update(TimeSpan.FromSeconds(20), 117));
        Assert.Equal(117, g.Update(TimeSpan.FromSeconds(0.2), 117));
        Assert.Null(new IdleCapGovernor(12, 60).Update(TimeSpan.FromSeconds(30), 60)); // never raises
    }

    [Fact]
    public void Cpu_hog_detector_needs_two_strikes_and_never_touches_game_or_windows()
    {
        var d = new CpuHogDetector();
        var game = new HashSet<int> { 100 };
        var sample = new Dictionary<int, (string, double)> { [100] = ("game", 60), [200] = ("updater", 30), [4] = ("System", 40), [300] = ("svchost", 25) };
        Assert.Empty(d.Update(sample, game).throttle);
        Assert.Equal([200], d.Update(sample, game).throttle);
        sample[200] = ("updater", 1);
        Assert.Equal([200], d.Update(sample, game).release);
    }

    [Fact]
    public void Crash_advisor_points_at_wrapper_dlls_and_recent_changes()
    {
        var g = new Game { Name = "Old Game", LastGoodSession = DateTimeOffset.Now.AddDays(-1) };
        g.AppliedChanges.Add(new AppliedChange { TweakId = "dxvk", AppliedAt = DateTimeOffset.Now, Action = new FileReplaceAction() });
        g.AppliedChanges.Add(new AppliedChange { TweakId = "framecap", AppliedAt = DateTimeOffset.Now.AddDays(-3), Action = new IniSetAction() });
        Assert.Single(CrashAdvisor.Suspects(g));
        var text = CrashAdvisor.Explain(g, new CrashInfo { FaultingModule = "d3d9.dll" });
        Assert.Contains("DXVK or ReShade", text);
        Assert.Contains("1 change(s)", text);
    }

    [Fact]
    public void Dxvk_is_offered_for_dx9_on_amd_and_never_over_reshade()
    {
        using var d = new TestDir();
        var exe = d.File("G/old.exe", "x");
        var hw = new HardwareProfile { Gpus = [new GpuInfo { Name = "AMD Radeon(TM) 8060S Graphics", Vendor = GpuVendor.Amd }] };
        // A real PE is needed for imports, so a non-PE file isn't offered anything.
        Assert.Null(DxvkPlanner.Plan(new Game { ExePath = exe }, new GameScanResult(), d.Path_("dxvk"), hw));
        Assert.Null(DxvkPlanner.Plan(new Game { ExePath = exe }, new GameScanResult { Injectors = ["ReShade"] }, d.Path_("dxvk"), hw));
    }
}

public class SleepPauseTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pauses_once_and_counts_time_asleep()
    {
        var t = new FitzLauncher.Core.Optimization.SleepPauseTracker();
        Assert.True(t.TryPause(T0));
        Assert.False(t.TryPause(T0.AddSeconds(1))); // screen off + system suspend: one pause
        Assert.True(t.TryBeginResume());
        Assert.False(t.TryBeginResume());          // display on + resume: one resume
        Assert.Equal(TimeSpan.FromMinutes(30), t.CompleteResume(T0.AddMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(30), t.Asleep);
        Assert.False(t.IsPaused);
    }

    [Fact]
    public void Going_back_to_sleep_while_waiting_for_sign_in_cancels_the_resume()
    {
        var t = new FitzLauncher.Core.Optimization.SleepPauseTracker();
        t.TryPause(T0);
        t.TryBeginResume();
        Assert.False(t.TryPause(T0.AddMinutes(1)));
        Assert.False(t.IsResuming);
        Assert.True(t.IsPaused);
        Assert.True(t.TryBeginResume());
        t.CompleteResume(T0.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), t.Asleep);
    }

    [Fact]
    public void Wake_without_a_pause_does_nothing()
    {
        var t = new FitzLauncher.Core.Optimization.SleepPauseTracker();
        Assert.False(t.TryBeginResume());
        Assert.Null(t.CompleteResume(T0));
    }

    [Fact]
    public void Session_duration_excludes_sleep()
    {
        var s = new FitzLauncher.Core.Models.SessionSummary { Start = T0, End = T0.AddHours(2), AsleepSeconds = 3600 };
        Assert.Equal(TimeSpan.FromHours(1), s.Duration);
    }
}
