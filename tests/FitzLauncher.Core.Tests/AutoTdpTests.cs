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
    public void HitchingAtTheCapWhenMoreWattsDidNotHelpDoesNotPinThePowerLimit()
    {
        // From a real session log: 100 FPS cap held exactly, 1% low 60-98 from streaming hitches, limit pinned at 55 W for 14 minutes
        // (a raise had already been tried and changed nothing). The game was smooth at 44-49 W.
        var g = new AutoTdpGovernor(Target, 25, 60, startWatts: 55);
        for (double t = 0; t < 2400; t += 2)
        {
            var low = Target * (0.70 + 0.04 * ((t / 2) % 3)); // ragged but not improving with power: 70-78 % of the target
            g.Update(new AutoTdpSample(600, Target, low, g.Current), T0.AddSeconds(t));
        }
        Assert.True(g.Current <= 48, $"still pinned at {g.Current} W");
    }

    [Fact]
    public void HitchRaiseIsNotUndoneWhenTheAverageFellAfterItAndIsRecovering()
    {
        // From a real session log (Witcher 3, 100 FPS cap): at 44 W the average sat on the cap with a 1% low of 56, so it raised +8 W. A heavier
        // scene arrived at the same moment: average 95, 91, 92, then 96.5 as the extra watts took hold. The judge compared 96.5 with the capped
        // 100 from before the raise (an average on the cap can never "improve"), called it a loss, went back to 44 W, and the game then ran at
        // 80-88 FPS for 40 s until two more raises took it to 60 W.
        var g = new AutoTdpGovernor(100, 25, 51, startWatts: 44); // 51 W: the ceiling the log had after an earlier heat episode
        g.Update(new AutoTdpSample(600, 100.0, 55.9, 43.9, 76), T0);
        Assert.True(g.Current > 44, "the 1% low dip should have raised the limit");
        var raised = g.Current;
        double[] avg = [94.9, 90.6, 91.9, 96.5, 97.0, 97.5];
        double[] low = [29.3, 24.7, 56.1, 45.9, 50.0, 55.0];
        for (var i = 0; i < avg.Length; i++)
            g.Update(new AutoTdpSample(550, avg[i], low[i], g.Current, 77), T0.AddSeconds(2 * (i + 1)));
        Assert.True(g.Current >= raised, $"went back to {g.Current} W while the average was recovering at {raised} W");
    }

    [Fact]
    public void DoesNotProbeForPowerWhenTheDrawJustCollapsedDuringALoad()
    {
        // From a real session log (Witcher 3, 100 FPS cap): the chip drew 57, 50 W, then 23 W for a loading transition; the 1% low was 86 with the
        // average on the cap. It "tested" +5 W because 23 of 53 W looked like an under-reporting sensor, then took it back 8 s later as hitching.
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 53);
        g.Update(new AutoTdpSample(610, 100, 91.0, 57.3, 78), T0);
        g.Update(new AutoTdpSample(602, 100, 91.6, 50.0, 79), T0.AddSeconds(2));
        var d = g.Update(new AutoTdpSample(593, 100, 86.5, 22.8, 68), T0.AddSeconds(4));
        Assert.Equal(53, g.Current);
        Assert.Null(d?.Watts);
    }

    [Fact]
    public void StepsDownFasterWhenTheGameDrawsClearlyUnderTheLimit()
    {
        // The game draws 49.5 W under a 53 W limit (3.5 W gap: too small for the snap-to-draw rule) with the cap held and room to spare.
        // 2 W per 40 s took minutes to find the level; 3 W per 25 s while the limit is clearly not what the game uses.
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 53);
        for (double t = 0; t <= 30; t += 2) g.Update(new AutoTdpSample(600, 100, 96, 49.5, 70), T0.AddSeconds(t));
        Assert.True(g.Current <= 50, $"still at {g.Current} W after 30 s");
        Assert.True(g.Current >= 50, $"went under what the game draws: {g.Current} W");
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
    public void AHitchAtTheCapThatComesBackJustAsDeepAfterARaiseGivesTheWattsBack()
    {
        // From a real Witcher 3 log (100 FPS cap, trigger 87 °C): 1% low 47 with the average on the cap raised 49 → 57 W. Six seconds later
        // the hitch had passed by itself (1% low 89), which counted as "helped"; 14 s after the raise it dipped to 49 again at 57 W,
        // the CPU went to 95 °C and the thermal guard took the cap down to 80. The same pattern came before 10 of 13 heat spikes.
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 49, thermalTriggerC: 87);
        var t = 0;
        for (; t < 30; t += 2) g.Update(new AutoTdpSample(600, 100, 92, 49, TempC: 81), T0.AddSeconds(t));
        Assert.Equal(49, g.Current);
        Assert.Equal(57, g.Update(new AutoTdpSample(600, 100, 47.0, 49, TempC: 81.8), T0.AddSeconds(t))?.Watts); t += 2;   // the raise in the log
        // At 57 W, as logged: the hitch passes, then comes back as deep as before.
        foreach (var (low, temp) in new[] { (47.0, 83.0), (81.6, 83), (88.8, 83), (92.0, 83), (91.0, 85), (61.2, 85), (49.1, 92) })
        {
            g.Update(new AutoTdpSample(600, 100, low, g.Current, TempC: temp), T0.AddSeconds(t));
            t += 2;
        }
        Assert.Equal(49, g.Current);
        // More hitches straight after don't buy the watts back.
        for (var end = t + 30; t < end; t += 2) g.Update(new AutoTdpSample(600, 100, t % 6 == 0 ? 52 : 90, g.Current, TempC: 84), T0.AddSeconds(t));
        Assert.Equal(49, g.Current);
    }

    [Fact]
    public void ARaiseAtTheCapThatKeepsTheOnePercentLowUpIsKept()
    {
        // The other side: after a raise the 1% low stays well above where it was, so the extra watts did help and stay.
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 49, thermalTriggerC: 87);
        var t = 0;
        for (; t < 30; t += 2) g.Update(new AutoTdpSample(600, 100, 92, 49, TempC: 78), T0.AddSeconds(t));
        Assert.Equal(54, g.Update(new AutoTdpSample(600, 100, 84, 49, TempC: 78), T0.AddSeconds(t))?.Watts); t += 2;
        // The 6 s FPS window still holds the dip just after the raise; that is not the dip coming back.
        g.Update(new AutoTdpSample(600, 100, 84, 54, TempC: 78), T0.AddSeconds(t)); t += 2;
        for (var end = t + 40; t < end; t += 2) g.Update(new AutoTdpSample(600, 100, 91, g.Current, TempC: 78), T0.AddSeconds(t));
        Assert.Equal(54, g.Current);
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
        // From a real log: 34 FPS against a 110 target at 37 W, 82 °C, trigger 86 °C. "Warm" used to block every raise.
        // (Within 2 °C of the trigger a collapse is heat, and extra power is withheld: see AFpsCollapseAtNearTriggerHeat...)
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 37, thermalTriggerC: 86);
        var d = g.Update(new AutoTdpSample(500, 34, 33, 37, TempC: 82), T0);
        Assert.NotNull(d?.Watts);
        Assert.InRange(d!.Watts!.Value, 38, 42);   // gently: 5 W at most while warm
    }

    [Fact]
    public void AFpsCollapseDuringASuddenCpuHeatSpikeGetsNoExtraPowerAndIsCutSooner()
    {
        // From a real session log (The Witcher 3, 100 FPS target, trigger 87 °C): the CPU jumped 85 → 94 °C in 6 s and FPS fell to 34.
        // The 16 s average lagged the spike, so it added 5 W at 91 °C, then held 6 s more before the first hard cut at 107 s.
        (int t, double fps, double low, double drawn, double temp)[] rows =
        [
            (73, 100.0, 96.6, 36.5, 83), (75, 100.0, 94.6, 33.7, 84), (77, 100.0, 93.3, 36.7, 84), (79, 100.0, 93.1, 33.1, 84),
            (81, 100.0, 93.1, 31.8, 84), (83, 99.0, 94.0, 33.4, 84), (85, 100.0, 95.1, 34.0, 84), (87, 100.0, 97.4, 36.6, 85),
            (89, 100.0, 100.1, 42.9, 85), (91, 100.0, 99.4, 44.1, 85), (93, 100.0, 55.6, 46.7, 86), (95, 93.6, 33.7, 46.6, 86),
            (97, 71.4, 33.6, 45.8, 87), (99, 46.4, 28.1, 45.8, 91), (101, 34.5, 29.8, 50.7, 94), (103, 34.5, 28.8, 48.0, 91),
            (105, 34.6, 29.8, 48.2, 90), (107, 34.8, 33.3, 48.0, 91),
        ];
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 47, thermalTriggerC: 87);
        int highest = g.Current, at103 = g.Current;
        foreach (var (t, fps, low, drawn, temp) in rows)
        {
            g.Update(new AutoTdpSample(600, fps, low, Math.Min(drawn, g.Current), TempC: temp), T0.AddSeconds(t));
            highest = Math.Max(highest, g.Current);
            if (t == 103) at103 = g.Current;
        }
        Assert.Equal(47, highest);       // no power added while the chip is 4 °C over the trigger and climbing
        Assert.True(at103 <= 39, $"still {at103} W at 103 s: the hard cut should not wait for the 16 s average");
    }

    [Fact]
    public void ASecondWarmUpWhileStillCutDoesNotLowerTheCeilingForTheClimbBack()
    {
        // From a real session log (The Witcher 3, 100 FPS target, trigger 87 °C): heat at 49 W was cut to 26 W, the chip cooled, then warmed
        // again briefly at 26 W. That counted as heat "starting" at 26 W, so the climb back stopped at 29-30 W: 3 minutes at 70 FPS and 67 °C.
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 49, thermalTriggerC: 87);
        AutoTdpSample At(double temp)
        {
            var fps = Math.Min(100, 2.4 * g.Current); // 30 W → 72 FPS, as logged; 42 W holds the target
            return new AutoTdpSample(500, fps, fps * 0.75, g.Current, TempC: temp);
        }
        var t = 0;
        void Tick(double temp) { g.Update(At(temp), T0.AddSeconds(t)); t += 2; }
        for (var i = 0; i < 6; i++) Tick(85);
        for (var i = 0; i < 26; i++) Tick(95);                          // CPU spike: cut from 49 W to 26 W
        // As logged from 2652 s: cools briefly (the episode ends), warms again at 26 W, then cools for good.
        foreach (var c in new double[] { 86, 80, 78, 76, 76, 83, 87, 89, 87, 89, 89, 88, 88, 88, 86, 86, 87, 83, 75, 71, 70, 69, 69 }) Tick(c);
        for (var i = 0; i < 60; i++) Tick(67);                           // clearly cool, FPS well under the target
        Assert.True(g.Current >= 42, $"stuck at {g.Current} W at 67 °C and {2.4 * g.Current:0} FPS");
        Assert.True(g.Current <= 44);                                    // still not straight back to the 49 W that heated it
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
    public void ASingleTemperatureSpikeDoesNotCutPowerButASustainedOneDoes()
    {
        // From a real log: 76 -> 89 -> 83 -> 78 °C within a few seconds, each time followed by a cut, a dip and a re-raise.
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 48, thermalTriggerC: 86.5);
        Assert.Null(g.Update(new AutoTdpSample(500, 118, 106, 47, TempC: 77), T0)?.Watts);
        Assert.Null(g.Update(new AutoTdpSample(500, 118, 106, 47, TempC: 77), T0.AddSeconds(2))?.Watts);
        Assert.Null(g.Update(new AutoTdpSample(500, 118, 106, 47, TempC: 90), T0.AddSeconds(4))?.Watts);   // one spike
        Assert.Equal(48, g.Current);

        var hot = new AutoTdpGovernor(110, 25, 60, startWatts: 48, thermalTriggerC: 86.5);
        hot.Update(new AutoTdpSample(500, 118, 106, 47, TempC: 77), T0);
        hot.Update(new AutoTdpSample(500, 118, 106, 47, TempC: 77), T0.AddSeconds(2));
        // Heat is judged over ~16 s: it takes several hot readings in a row before the average crosses the trigger.
        for (var t = 4; t <= 10; t += 2) Assert.Null(hot.Update(new AutoTdpSample(500, 118, 106, 47, TempC: 91), T0.AddSeconds(t))?.Watts);
        var cut = hot.Update(new AutoTdpSample(500, 118, 106, 47, TempC: 92), T0.AddSeconds(12));
        Assert.True(cut?.Watts < 48);

        var extreme = new AutoTdpGovernor(110, 25, 60, startWatts: 48, thermalTriggerC: 86.5);
        Assert.True(extreme.Update(new AutoTdpSample(500, 118, 106, 47, TempC: 95), T0)?.Watts < 48);       // 8 °C+ past the trigger acts alone
    }

    [Fact]
    public void WhenWellOverTheTargetItStepsDownFasterAndByMore()
    {
        // Log: 121 FPS against a 110 target at a steady 47 W for a minute, 1% low 101-107 (just under the old 94 % bar).
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 47);
        for (var t = 0; t < 70; t += 2) g.Update(new AutoTdpSample(500, 121, 104, g.Current), T0.AddSeconds(t));
        Assert.True(g.Current <= 44, $"still at {g.Current}");
        // But not when it is only just on target.
        var tight = new AutoTdpGovernor(110, 25, 60, startWatts: 47);
        for (var t = 0; t < 70; t += 2) tight.Update(new AutoTdpSample(500, 110, 104, tight.Current), T0.AddSeconds(t));
        Assert.True(tight.Current >= 45);
    }

    [Fact]
    public void AHitchInTheOnePercentLowWithTheAverageAboveTargetDoesNotBuyPower()
    {
        // Log: 117 FPS against 110 with a 1% low of 73 raised the limit +8 W twice, to 57 W.
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 41);
        for (var t = 0; t < 30; t += 2) Assert.Null(g.Update(new AutoTdpSample(500, 117, 73, 41, TempC: 75), T0.AddSeconds(t))?.Watts);
        Assert.Equal(41, g.Current);
        // A real shortfall in the average still does.
        Assert.True(g.Update(new AutoTdpSample(500, 100, 70, 41, TempC: 75), T0.AddSeconds(40))?.Watts > 41);
    }

    [Fact]
    public void WhenTheChipIsClearlyCoolTheGuardHoldDoesNotStopItGettingPowerBack()
    {
        // Log: 31 W for 90 s at 69-72 °C with 84-106 FPS against 110, because thermal_hold stayed on while the FPS cap recovered.
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 31, thermalTriggerC: 86.5);
        var d = g.Update(new AutoTdpSample(500, 84, 74, 31, TempC: 70, ThermalHold: true), T0);
        Assert.True(d?.Watts > 31);
        // Not clearly cool (80 °C): the hold still applies.
        var warm = new AutoTdpGovernor(110, 25, 60, startWatts: 31, thermalTriggerC: 86.5);
        Assert.Null(warm.Update(new AutoTdpSample(500, 84, 74, 31, TempC: 80, ThermalHold: true), T0)?.Watts);
    }

    [Fact]
    public void AFpsCollapseWhileHotCutsPowerHardAndSoonThenItClimbsBack()
    {
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 49, thermalTriggerC: 86.5);
        g.Update(new AutoTdpSample(500, 120, 105, 49, TempC: 80), T0);
        var cuts = new List<int>();
        for (var t = 2; t < 20; t += 2)
            if (g.Update(new AutoTdpSample(500, 34, 33, g.Current, TempC: 93), T0.AddSeconds(t))?.Watts is { } w) cuts.Add(w);
        Assert.NotEmpty(cuts);
        Assert.True(49 - cuts[0] >= 8, $"first cut only {49 - cuts[0]} W");
        Assert.True(cuts.Count >= 2, "it should keep cutting every few seconds while it stays collapsed and hot");
        // Cooler and still short of the target: power comes back.
        var cool = g.Update(new AutoTdpSample(500, 40, 34, g.Current, TempC: 72), T0.AddSeconds(40));
        Assert.True(cool?.Watts > g.Current - 1 && cool!.Watts > cuts[^1]);
    }

    [Fact]
    public void AFpsCollapseAtNearTriggerHeatGetsNoExtraPowerAndTheCutDoesNotWaitForTheAverage()
    {
        // From a real session log (The Witcher 3, 100 FPS target, trigger 85 °C, 52 W): the 1% low fell to 29 at 99 s with the CPU at 85 °C, the
        // average followed (93, 74, 42 FPS). It added 5 W at 103 s (85 °C, 44 W drawn): no help, 30 FPS and 92 °C six seconds later; the first cut
        // came at 109 s. The same happened at 87 °C in another log. Every collapse in 7 sessions at 83+ °C ended only by cooling.
        (int t, double fps, double low, double drawn, double temp)[] rows =
        [
            (91, 100.0, 98.1, 41.1, 83), (93, 100.0, 97.1, 36.5, 84), (95, 100.0, 97.1, 36.5, 84), (97, 100.0, 97.1, 36.5, 84),
            (99, 93.3, 29.4, 47.0, 85), (101, 73.9, 29.2, 47.0, 85), (103, 41.5, 29.0, 44.1, 85), (105, 29.7, 29.2, 49.4, 86),
            (107, 29.9, 28.5, 50.0, 87), (109, 29.9, 28.5, 56.0, 92),
        ];
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 52, thermalTriggerC: 85);
        int highest = g.Current, at103 = g.Current;
        foreach (var (t, fps, low, drawn, temp) in rows)
        {
            g.Update(new AutoTdpSample(600, fps, low, Math.Min(drawn, g.Current), TempC: temp), T0.AddSeconds(t));
            highest = Math.Max(highest, g.Current);
            if (t == 103) at103 = g.Current;
        }
        Assert.Equal(52, highest);       // never above where it was
        Assert.True(at103 <= 44, $"still {at103} W at 103 s: the cut should start when the 1% low collapses at trigger heat");
    }

    [Fact]
    public void AFiftyOnePercentLowCollapseWhileHotCutsHardEvenThoughTheAverageIsStillOnTarget()
    {
        // From a real session log (trigger 85 °C, 60 W): 100 FPS with the CPU at 87-89 °C for 14 s, then the 1% low fell to 29 at 90 s with the
        // average still 100. It cut only 3 W; FPS was 30 by 96 s, and the chip reached 94 °C before the limit got to 41 W.
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 60, thermalTriggerC: 85);
        (int t, double temp)[] warmUp = [(62, 68), (64, 68), (66, 68), (68, 68), (70, 68), (72, 67), (74, 83), (76, 86), (78, 87), (80, 87), (82, 87), (84, 88), (86, 88), (88, 88)];
        // Draw 58 W (the log read 52 W): keeps this about the heat cut; the faster unused-headroom step-down would otherwise land a 3 W step at 87 s, inside the 5 s spacing before the cut.
        foreach (var (t, temp) in warmUp) g.Update(new AutoTdpSample(600, 100, 100, 58, TempC: temp), T0.AddSeconds(t));
        var before = g.Current;
        g.Update(new AutoTdpSample(600, 100, 29.2, 52.7, TempC: 89), T0.AddSeconds(90));
        Assert.True(before - g.Current >= 8, $"only cut {before - g.Current} W when the 1% low collapsed at 89 °C");
    }

    [Fact]
    public void AHitchWithTheAverageOnTargetAndOneHotReadingDoesNotCutHard()
    {
        // From a real session log (The Witcher 3, 100 FPS target, trigger 85 °C, 58 W): the average sat on the cap while the 1% low fell to 33
        // (a hitch) and the CPU read 91 °C for one tick after 76-77 °C. A single reading is not heat: it cut 8 W, then 8 W more on the next hitch.
        (int t, double fps, double low, double drawn, double temp)[] rows =
        [
            (128, 100.0, 78.5, 58.0, 76), (130, 100.0, 79.3, 58.0, 76), (132, 100.0, 83.3, 58.0, 77), (134, 100.0, 82.1, 58.0, 77),
            (136, 100.0, 86.1, 58.0, 77), (138, 100.0, 86.8, 58.0, 76), (140, 100.0, 81.8, 57.0, 76), (142, 100.0, 61.3, 58.5, 88),
            (144, 100.0, 51.6, 57.1, 90), (146, 100.0, 32.6, 48.5, 91),
        ];
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 58, thermalTriggerC: 85);
        foreach (var (t, fps, low, drawn, temp) in rows)
            g.Update(new AutoTdpSample(600, fps, low, Math.Min(drawn, g.Current), TempC: temp), T0.AddSeconds(t));
        Assert.True(58 - g.Current < 8, $"cut {58 - g.Current} W for a hitch on one hot reading");
    }

    [Fact]
    public void HardCutsForALowOnlyCollapseAreSpacedLikeOtherHeatCuts()
    {
        // From the same log: the chip really was at 88-92 °C for 20 s, so the 1% low collapse at 198 s rightly cut 8 W; the average was still 100
        // when the next hitch at 208 s cut 8 W again (42 → 34 W) 10 s later, and FPS then really did fall to 29. One hard cut, then wait.
        // (The 1% lows before 198 s are 98 here: the log's own lows made a cold replay raise power first, which is not what this is about.)
        (int t, double fps, double low, double drawn, double temp)[] rows =
        [
            (176, 100.0, 98.7, 50.0, 76), (178, 100.0, 98.7, 50.0, 76), (180, 100.0, 97.8, 49.8, 76), (182, 100.0, 98.0, 50.1, 76),
            (184, 100.0, 98.0, 50.0, 75), (186, 100.0, 98.0, 50.0, 76), (188, 100.0, 98.0, 46.1, 88), (190, 100.0, 98.0, 48.4, 91),
            (192, 100.0, 98.0, 46.6, 91), (194, 100.0, 98.0, 50.3, 84), (196, 100.0, 98.0, 47.4, 91), (198, 100.0, 32.2, 45.6, 91),
            (200, 100.0, 58.3, 42.0, 91), (202, 100.0, 92.3, 42.0, 90), (204, 100.0, 68.0, 42.0, 91), (206, 100.0, 60.1, 42.0, 91),
            (208, 100.0, 27.4, 42.2, 92),
        ];
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 50, thermalTriggerC: 85);
        var afterFirstCut = -1;
        foreach (var (t, fps, low, drawn, temp) in rows)
        {
            g.Update(new AutoTdpSample(600, fps, low, Math.Min(drawn, g.Current), TempC: temp), T0.AddSeconds(t));
            if (t == 198) afterFirstCut = g.Current;
        }
        Assert.True(afterFirstCut <= 42, $"the collapse at 198 s should have cut hard (limit {afterFirstCut} W)");
        Assert.Equal(afterFirstCut, g.Current); // nothing more between 198 and 208 s
    }

    [Fact]
    public void AHitchWithACoolChipIsNotTreatedAsHeat()
    {
        // From a real session log: a loading hitch (1% low 23, average 100) at 68 °C is a probe raise, not a hard cut.
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 52, thermalTriggerC: 85);
        g.Update(new AutoTdpSample(600, 100, 99.1, 24.3, TempC: 68), T0.AddSeconds(68));
        var d = g.Update(new AutoTdpSample(600, 100, 22.9, 24.1, TempC: 68), T0.AddSeconds(70));
        Assert.True(d?.Watts > 52);
    }

    [Fact]
    public void HeatCutsAreSpacedForTheSlowCoolingAndStayCloseToWhereTheHeatStarted()
    {
        // Log: 47 W -> 26 W in 54 s (a cut every 12-14 s) while the chip stayed at 85-94 °C and then cooled by itself.
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 47, thermalTriggerC: 86.5);
        var cuts = 0;
        var lowest = 47;
        for (var t = 0; t < 150; t += 2)
        {
            if (g.Update(new AutoTdpSample(500, 118, 106, g.Current, TempC: 91), T0.AddSeconds(t))?.Watts is not null) cuts++;
            lowest = Math.Min(lowest, g.Current);
        }
        Assert.True(lowest >= 35, $"fell to {lowest} W");        // within 12 W of where the heat started
        Assert.InRange(cuts, 1, 4);
    }

    [Fact]
    public void AfterAHeatEpisodeItClimbsBackToNearWhereItStartedNotJustFourWattsAboveTheFloor()
    {
        // Log: cooled at 26 W, ceiling 30 W for 5 minutes at 64 °C with 90-105 FPS against 110.
        var g = new AutoTdpGovernor(110, 25, 60, startWatts: 47, thermalTriggerC: 86.5);
        for (var t = 0; t < 90; t += 2) g.Update(new AutoTdpSample(500, 118, 106, g.Current, TempC: 91), T0.AddSeconds(t));
        var floor = g.Current;
        Assert.True(floor < 47);
        for (var t = 90; t < 150; t += 2) g.Update(Plant(g.Current, needed: 47) with { TempC = 64 }, T0.AddSeconds(t));
        Assert.True(g.Current >= 42, $"stuck at {g.Current} W (it cooled from {floor} W; the heat began at 47 W)");
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

    [Fact]
    public void ACoolReadingIsNotCutAgainBecauseTheHeatAverageIsStale()
    {
        // From a real session log (Witcher 3, 100 FPS target, trigger 85 °C, 19 Nov 2026): the CPU sat at 94-95 °C for 30 s while the limit came down
        // 54 → 39 W, then a scene change dropped it to 75-78 °C within 4 s. The 16 s average still read 88, so the 1% low collapsing (the limit was
        // now too low for the new scene) was called heat and cut 8 W more, to 25-29 W; FPS fell to 20-55 and four raises were needed to climb back.
        // That happened three times in 22 minutes (603 s, 759 s, 1067 s). A reading well under the trigger must act on its own, not on the average.
        var g = new AutoTdpGovernor(100, 25, 60, startWatts: 54, thermalTriggerC: 85);
        double t = 0;
        for (; t < 30; t += 2) g.Update(new AutoTdpSample(600, 100, 90, g.Current, 95), T0.AddSeconds(t)); // hot, capped and smooth
        var atCooling = g.Current;
        Assert.True(atCooling < 54, "the heat should have cut power");
        for (var i = 0; i < 4; i++, t += 2)
        {
            g.Update(new AutoTdpSample(600, 56, 28, g.Current, 75), T0.AddSeconds(t)); // cooled to 75 °C, new scene needs more than the limit
            Assert.True(g.Current >= atCooling, $"cut to {g.Current} W at 75 °C on a stale heat average");
        }
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
