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
    public void DoesNotAddWattsButTrimsTheLimitWhenThePowerIsNotTheLimit()
    {
        // Engine-bound: slow whatever the limit (110 of 117 FPS), and the chip draws only 45 W of its 60 W limit.
        var g = new AutoTdpGovernor(Target, 25, 60, 60);
        for (var t = 0; t < 400; t += 2)
        {
            g.Update(new AutoTdpSample(500, 110, 100, 45), T0.AddSeconds(t));
            Assert.True(g.Current <= 60);
        }
        Assert.InRange(g.Current, 49, 55); // 45 W drawn + 4 W: a lower limit costs nothing; a short test raise may be in progress
    }

    [Fact]
    public void RaisesFastEvenWhenThePowerReadingSaysTheLimitIsNotBinding()
    {
        // Seen on device: limit 29 W, reading 25 W, 84 of 117 FPS. The reading under-reports: the game really needs 45 W.
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 29);
        double? reached = null;
        for (var t = 0; t < 60; t += 2)
        {
            var s = Plant(g.Current, needed: 45, drawnOverride: g.Current - 4);
            g.Update(s, T0.AddSeconds(t));
            if (g.Current >= 45 && reached is null) reached = t;
        }
        Assert.NotNull(reached);
        Assert.True(reached <= 16, $"took {reached} s to reach 45 W");
    }

    [Fact]
    public void BigShortfallsGetBiggerSteps()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 29);
        var d = g.Update(new AutoTdpSample(500, 70, 60, 25), T0);           // 60 % of target
        Assert.Equal(39, d!.Watts);                                          // +10 W
        var g2 = new AutoTdpGovernor(Target, 25, 60, startWatts: 29);
        Assert.Equal(34, g2.Update(new AutoTdpSample(500, 108, 100, 29), T0)!.Watts); // small dip: +5 W
    }

    [Fact]
    public void OnceARaiseHelpedTheUnderReportingReadingNoLongerPullsPowerDown()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 29);
        for (var t = 0; t < 400; t += 2) g.Update(Plant(g.Current, needed: 45, drawnOverride: g.Current - 6), T0.AddSeconds(t));
        Assert.InRange(g.Current, 41, 52);   // never snapped down to "draw + 4" (about 31 W)
    }

    [Fact]
    public void AProbeThatDoesNotHelpIsUndoneAndThenTheLimitIsTrimmedToWhatIsDrawn()
    {
        // Engine-bound at 100 FPS whatever the limit; the chip really draws 30 W of its 50 W limit.
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 50);
        var top = 50;
        for (var t = 0; t < 400; t += 2)
        {
            g.Update(new AutoTdpSample(500, 100, 90, 30), T0.AddSeconds(t));
            top = Math.Max(top, g.Current);
        }
        Assert.True(top > 50, "it should have tested a higher limit first");
        Assert.InRange(g.Current, 33, 45);   // 30 W drawn + 4 W, or inside a short retest above it
    }

    [Fact]
    public void MenuFramesInTheBeforeAverageDoNotMakeARealImprovementLookLikeALoss()
    {
        // Just left a menu at the 25 W floor: the 6 s average still holds menu frames, the 1% low is the game's.
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 25);
        Assert.Equal(35, g.Update(new AutoTdpSample(500, 70, 40, 25), T0)!.Watts);       // +10 W
        var later = g.Update(new AutoTdpSample(500, 66, 52, 33), T0.AddSeconds(6));      // average fell, 1% low rose 30 %
        Assert.True(later is null || later.Watts > 35, "must not undo the raise");
        Assert.True(g.Current >= 35);
    }

    [Fact]
    public void AFailedTestRaiseIsRetriedWithinTwoMinutesNotParkedForMinutes()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 25);
        var raises = 0;
        var last = g.Current;
        for (var t = 0; t < 200; t += 2)
        {
            g.Update(new AutoTdpSample(500, 60, 50, 24), T0.AddSeconds(t));              // nothing helps this scene
            if (g.Current > last) raises++;
            last = g.Current;
        }
        Assert.True(raises >= 2, $"only {raises} raise(s) in 200 s");
    }

    [Fact]
    public void AFpsCollapseWhileWarmStillGetsPowerBackInsteadOfBeingHeldDown()
    {
        // From a real log: 34 FPS against a 110 target at 37 W, 84 °C, trigger 86 °C. "Warm" used to block every raise.
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 37, thermalTriggerC: 86);
        var d = g.Update(new AutoTdpSample(500, 34, 33, 37, TempC: 84), T0);
        Assert.NotNull(d?.Watts);
        Assert.InRange(d!.Watts!.Value, 38, 42);   // gently: 5 W at most while warm
    }

    [Fact]
    public void ItDoesNotCutPowerForHeatWhileFpsHasAlreadyCollapsedUnlessItIsWellOverTheTrigger()
    {
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 40, thermalTriggerC: 86);
        var held = g.Update(new AutoTdpSample(500, 34, 33, 40, TempC: 87), T0);
        Assert.Null(held?.Watts);                                    // 1 °C over the trigger, FPS at 31 %: no cut
        var hard = g.Update(new AutoTdpSample(500, 34, 33, 40, TempC: 91), T0.AddSeconds(14));
        Assert.True(hard?.Watts < 40);                               // 5 °C over: heat comes first
    }

    [Fact]
    public void HeatStillCutsPowerWhenTheGameIsRunningFine()
    {
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 48, thermalTriggerC: 86);
        var d = g.Update(new AutoTdpSample(500, 107, 87, 48, TempC: 87), T0);
        Assert.True(d?.Watts < 48);
    }

    [Fact]
    public void AQuietMenuCannotDragTheLimitFarBelowWhatTheGameUsuallyNeeds()
    {
        // Last time this game held the target at 41 W. In a menu the draw is 22 W and FPS is easy: it may ease off, not dive.
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 44, learnedTypical: 41);
        for (var t = 0; t < 300; t += 2) g.Update(new AutoTdpSample(500, Target, Target * 0.98, 22), T0.AddSeconds(t));
        Assert.InRange(g.Current, 35, 44);
        // Without a known level the same menu does pull it down (existing behaviour).
        var plain = new AutoTdpGovernor(Target, 25, 60, startWatts: 44);
        for (var t = 0; t < 300; t += 2) plain.Update(new AutoTdpSample(500, Target, Target * 0.98, 22), T0.AddSeconds(t));
        Assert.True(plain.Current < 35);
    }

    [Fact]
    public void TheFloorFollowsWhatARealDipThisSessionForcedItUpTo()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 40);
        // A heavy scene needs 50 W: it climbs there.
        for (var t = 0; t < 60; t += 2) g.Update(Plant(g.Current, needed: 50), T0.AddSeconds(t));
        Assert.True(g.Current >= 49, $"only reached {g.Current}");
        var peak = g.Current;
        // Then a quiet menu for five minutes: it may ease off but not dive to the floor.
        for (var t = 60; t < 360; t += 2) g.Update(new AutoTdpSample(500, Target, Target * 0.98, 20), T0.AddSeconds(t));
        Assert.True(g.Current >= peak - 12, $"fell from {peak} to {g.Current}");
    }

    [Fact]
    public void WhenALimitCouldNotBeAppliedItGoesBackToWhatIsReallySetAndTriesAgain()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 30);
        var d = g.Update(new AutoTdpSample(500, 70, 60, 30), T0);          // asks for +10 W
        Assert.Equal(40, d!.Watts);
        g.Resync(30);                                                       // RyzenAdj timed out: still 30 W
        Assert.Equal(30, g.Current);
        var again = g.Update(new AutoTdpSample(500, 70, 60, 30), T0.AddSeconds(4));
        Assert.True(again?.Watts > 30);                                     // asks again, nothing is stuck waiting on the lost raise
    }

    [Fact]
    public void LowersTheLimitWhenLowRealisticOneLowsStillMeetTheFloorWithRoomToSpare()
    {
        var g = new AutoTdpGovernor(Target, 25, 60, 45);
        // 1% low at 95 % of target: smooth (floor 90 %) with some room. Draw sits at the limit, so it creeps down.
        for (var t = 0; t < 200; t += 2) g.Update(new AutoTdpSample(500, 117, 111, g.Current), T0.AddSeconds(t));
        Assert.True(g.Current < 45);
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
        Assert.True(g.Current <= 48, $"ended at {g.Current}");   // may be inside a short retest of a higher limit
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
            Assert.Null(warm.Update(new AutoTdpSample(500, 90, 80, 40, TempC: 87), T0.AddSeconds(t))?.Watts); // a normal dip at 85-90 °C: no more power
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

public class AutoTdpGateTests
{
    [Theory]
    [InlineData(AutoTdpMode.Off, true, false, true, true, null)]
    [InlineData(AutoTdpMode.On, true, false, true, true, null)]
    [InlineData(AutoTdpMode.On, false, false, true, true, "this PC isn't a Ryzen APU handheld")]
    [InlineData(AutoTdpMode.On, true, true, true, true, "the launcher is in safe mode")]
    [InlineData(AutoTdpMode.On, true, false, false, true, "power management is off for this game (game page → Power limit → tick Manage)")]
    [InlineData(AutoTdpMode.Observe, true, false, true, false, "RyzenAdj isn't set up (Settings → Power limit → Browse… to ryzenadj.exe)")]
    public void SaysWhyAutoTdpCannotRun(AutoTdpMode mode, bool apu, bool safe, bool managed, bool ryzenAdj, string? expected) =>
        Assert.Equal(expected, AutoTdpGate.WhyNot(mode, apu, safe, managed, ryzenAdj));
}
