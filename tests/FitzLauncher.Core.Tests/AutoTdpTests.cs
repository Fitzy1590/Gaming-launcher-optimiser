using FitzLauncher.Core.Optimization;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class AutoTdpTests
{
    private const int Target = 117;
    private static readonly DateTime T0 = new(2026, 1, 1);

    /// <summary>A fake game: needs <paramref name="needed"/> W to hold the target; FPS falls in proportion below that.</summary>
    private static AutoTdpSample Plant(int limit, double needed, double? engineLimit = null, double? drawnOverride = null)
    {
        var usable = Math.Min(limit, engineLimit ?? double.MaxValue);
        var ratio = Math.Min(1, usable / needed);
        var fps = Target * ratio;
        var low = ratio >= 1 ? Target * 0.98 : fps * 0.9; // below the need, the 1% lows suffer first
        return new AutoTdpSample(500, fps, low, drawnOverride ?? Math.Min(limit, engineLimit ?? needed));
    }

    private sealed record Tick(double T, int Limit, bool Distress);

    private static List<Tick> Run(AutoTdpGovernor g, Func<double, AutoTdpSample> world, double seconds, double from = 0)
    {
        var ticks = new List<Tick>();
        for (var t = from; t < from + seconds; t += 2)
        {
            var s = world(t);
            ticks.Add(new Tick(t, g.Current, s.LowFps < Target * 0.9));
            g.Update(s with { }, T0.AddSeconds(t));
        }
        return ticks;
    }

    [Fact]
    public void SettlesJustAboveWhatTheGameNeedsWithAFewShortDips()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 45);
        // The world must see the limit the governor holds *now*:
        var ticks = new List<Tick>();
        for (var t = 0; t < 900; t += 2)
        {
            var s = Plant(g.Current, needed: 30);
            ticks.Add(new Tick(t, g.Current, s.LowFps < Target * 0.9));
            g.Update(s, T0.AddSeconds(t));
        }
        Assert.InRange(g.Current, 30, 36);
        Assert.True(ticks.Count(x => x.Distress) <= ticks.Count * 0.04, $"too many dips: {ticks.Count(x => x.Distress)}");
        Assert.All(ticks, x => Assert.InRange(x.Limit, 25, 60));
        Assert.True(g.Current < 45); // it did come down from the starting point
    }

    [Fact]
    public void RaisesFastWhenABigSceneArrivesThenEasesBackSlowly()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 36);
        double need(double t) => t < 300 ? 30 : t < 420 ? 52 : 30; // calm, a fight, calm again
        var maxAfterJump = 0;
        double reached52 = double.NaN;
        for (double t = 0; t < 1500; t += 2)
        {
            var s = Plant(g.Current, need(t));
            g.Update(s, T0.AddSeconds(t));
            if (t >= 300 && t < 420) { maxAfterJump = Math.Max(maxAfterJump, g.Current); if (g.Current >= 52 && double.IsNaN(reached52)) reached52 = t - 300; }
        }
        Assert.InRange(reached52, 0, 20);               // up within seconds
        Assert.True(g.Current <= 42, $"should have eased back, is {g.Current}"); // and back down after
    }

    [Fact]
    public void DoesNotAddWattsWhenThePowerIsNotTheLimit()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 45);
        for (var t = 0; t < 300; t += 2)
        {
            // Engine-bound: slow whatever the limit, and the chip draws only 25 W.
            g.Update(new AutoTdpSample(500, 80, 70, 25), T0.AddSeconds(t));
            Assert.True(g.Current <= 45);
        }
        Assert.Equal(45, g.Current);
    }

    [Fact]
    public void BacksOutWhenExtraWattsDoNotImproveFps()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 40);
        var top = 0;
        for (var t = 0; t < 400; t += 2)
        {
            var s = Plant(g.Current, needed: 50, engineLimit: 40); // can't use more than 40 W
            g.Update(s, T0.AddSeconds(t));
            top = Math.Max(top, g.Current);
        }
        Assert.True(g.Current <= 40, $"ended at {g.Current}");
        Assert.True(top < 60);
    }

    [Fact]
    public void StaysInsideTheConfiguredRange()
    {
        var hungry = new AutoTdpGovernor(Target, 25, 60, 30);
        for (var t = 0; t < 600; t += 2) { hungry.Update(Plant(hungry.Current, needed: 100), T0.AddSeconds(t)); Assert.InRange(hungry.Current, 25, 60); }
        Assert.Equal(60, hungry.Current);

        var light = new AutoTdpGovernor(Target, 25, 60, 50);
        for (var t = 0; t < 1200; t += 2) { light.Update(Plant(light.Current, needed: 5), T0.AddSeconds(t)); Assert.InRange(light.Current, 25, 60); }
        Assert.Equal(25, light.Current);
    }

    [Fact]
    public void DoesNotAddPowerWhileWarmOrWhileTheThermalGuardActs()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, 40);
        for (var t = 0; t < 60; t += 2)
            Assert.Null(g.Update(new AutoTdpSample(500, 70, 60, 40, TempC: 80, ThermalHold: true), T0.AddSeconds(t))?.Watts); // dipping at the limit, but guard active
        Assert.Equal(40, g.Current);
        var warm = new AutoTdpGovernor(Target, 25, 60, 40);
        for (var t = 0; t < 60; t += 2)
            Assert.Null(warm.Update(new AutoTdpSample(500, 70, 60, 40, TempC: 87), T0.AddSeconds(t))?.Watts); // 85-90 °C: no more power
        Assert.Equal(40, warm.Current);
    }

    [Fact]
    public void WhenHotItLowersPowerNotTheFpsTargetAndStopsOnceCoolEnough()
    {
        // Temperature follows power: 30 + 1.05 × limit (60 W → 93 °C). The game needs 50 W.
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 60, thermalTriggerC: 90);
        var guardMayAct = new List<bool>();
        for (var t = 0; t < 400; t += 2)
        {
            var s = Plant(g.Current, needed: 50) with { TempC = 30 + 1.05 * g.Current };
            guardMayAct.Add(!g.CanCoolByPower);
            g.Update(s, T0.AddSeconds(t));
        }
        Assert.InRange(g.Current, 52, 58);           // cooled to just under 90 °C, not slashed to the floor
        Assert.True(g.CanCoolByPower);               // …and the cap guard was told to wait while that happened
        Assert.False(guardMayAct[0]);
    }

    [Fact]
    public void AtTheFloorAndStillHotTheCapGuardIsToldItMayAct()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 26, thermalTriggerC: 90);
        for (var t = 0; t < 120; t += 2) g.Update(Plant(g.Current, 80) with { TempC = 95 }, T0.AddSeconds(t));
        Assert.Equal(25, g.Current);
        Assert.False(g.CanCoolByPower);
    }

    [Fact]
    public void AfterCoolingItDoesNotClimbStraightBackToThePowerThatOverheated()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 60, thermalTriggerC: 90);
        for (var t = 0; t < 120; t += 2) g.Update(Plant(g.Current, 50) with { TempC = 30 + 1.05 * g.Current }, T0.AddSeconds(t));
        var cooled = g.Current;
        // A heavy scene now asks for more, with the chip cool: it may rise a little, not back to 60.
        for (var t = 120; t < 240; t += 2) g.Update(Plant(g.Current, 70) with { TempC = 70 }, T0.AddSeconds(t));
        Assert.True(g.Current <= cooled + 5, $"went from {cooled} to {g.Current}");
    }

    [Fact]
    public void FollowsTheTargetItIsHolding()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, 45);
        g.SetTarget(100); // e.g. the cap was lowered
        for (var t = 0; t < 200; t += 2) g.Update(new AutoTdpSample(500, 100, 96, 35), T0.AddSeconds(t)); // 100 FPS is now healthy
        Assert.True(g.Current < 45);
    }

    [Fact]
    public void IgnoresMenusAndLoadingScreens()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, 40);
        for (var t = 0; t < 120; t += 2) Assert.Null(g.Update(new AutoTdpSample(3, 10, 5, 20), T0.AddSeconds(t)));
        Assert.Equal(40, g.Current);
    }

    [Fact]
    public void ObserveModeTracksWhatTheGameDrawsWithoutTouchingAnything()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 60, simulated: true);
        for (var t = 0; t < 600; t += 2) g.Update(new AutoTdpSample(500, 117, 115, 34), T0.AddSeconds(t)); // game draws a steady 34 W
        Assert.InRange(g.Current, 36, 44);                 // it would have held a little above the draw
        Assert.InRange(g.AverageLimit, 36, 60);
        Assert.Equal(34, g.AverageDrawn!.Value, 0);
        // A spike to 55 W: the virtual limit would have been hit, so it would have gone up.
        for (var t = 600; t < 640; t += 2) g.Update(new AutoTdpSample(500, 117, 115, 55), T0.AddSeconds(t));
        Assert.True(g.Current >= 55);
    }

    [Fact]
    public void SummaryDescribesTheRangeThatHeld()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, 45);
        for (var t = 0; t < 900; t += 2) g.Update(Plant(g.Current, needed: 32), T0.AddSeconds(t));
        var p = g.Summary()!;
        Assert.InRange(p.Typical, 32, 40);
        Assert.True(p.Min <= p.Typical && p.Typical <= p.Max);
        Assert.Null(new AutoTdpGovernor(Target, 25, 60, 45).Summary());
    }

    [Fact]
    public void ProfilesBlendTowardsNewSessions()
    {
        var a = new AutoTdpProfile { Min = 30, Typical = 40, Max = 50, Sessions = 2 };
        var b = AutoTdpProfile.Blend(a, new AutoTdpProfile { Min = 36, Typical = 46, Max = 60 });
        Assert.Equal(3, b.Sessions);
        Assert.InRange(b.Typical, 41, 43);
        Assert.Equal(60, b.Max);
        Assert.Equal(1, AutoTdpProfile.Blend(null, new AutoTdpProfile { Min = 30, Typical = 40, Max = 50 }).Sessions);
    }
}

public class RecentStatsTests
{
    [Fact]
    public void RecentWindowGivesAverageAndOnePercentLow()
    {
        var r = new FitzLauncher.Core.Fps.FrameTimeRecorder();
        for (var i = 0; i < 980; i++) r.AddFrame(8.5);   // ~117 fps
        for (var i = 0; i < 20; i++) r.AddFrame(40);      // 2 % of frames are slow, so the 1 % low is one of them
        var (frames, fps, low) = r.RecentStats(10);
        Assert.Equal(1000, frames);
        Assert.InRange(fps, 100, 117);
        Assert.InRange(low, 20, 30);                       // 1 % low ≈ 25 fps
        Assert.Equal((0, 0.0, 0.0), new FitzLauncher.Core.Fps.FrameTimeRecorder().RecentStats(10));
    }
}
