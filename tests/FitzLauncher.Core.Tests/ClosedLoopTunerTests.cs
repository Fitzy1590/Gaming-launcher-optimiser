using FitzLauncher.Core.Telemetry;

namespace FitzLauncher.Core.Tests;

public class ClosedLoopTunerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);
    private static readonly TunerContext Plain = new(false, false, false, true, 60);

    private static TunerSample S(double avg = 60, double low = 55, double? cpu = 8, double? gpu = 8, double? temp = 60, TunerContext? ctx = null)
        => new(new FrameBucket(0, 60, 0, avg, low, low, cpu, gpu), temp, temp, ctx ?? Plain);

    /// <summary>Runs one update per second; returns every action with the second it happened on.</summary>
    private static List<(int sec, TunerAction a)> Run(ClosedLoopTuner t, int from, int count, Func<int, TunerSample> sample)
    {
        var list = new List<(int, TunerAction)>();
        for (var i = from; i < from + count; i++)
            foreach (var a in t.Update(sample(i), T0.AddSeconds(i))) list.Add((i, a));
        return list;
    }

    private static TunerSample CpuBound(TunerContext? ctx = null) => S(cpu: 12, gpu: 8, ctx: ctx);
    private static TunerSample GpuBound(TunerContext? ctx = null) => S(cpu: 8, gpu: 12, ctx: ctx);

    [Fact]
    public void Cpu_bound_fires_on_the_third_second_not_the_second()
    {
        var t = new ClosedLoopTuner();
        var acts = Run(t, 0, 2, _ => CpuBound());
        Assert.Empty(acts);
        acts = Run(t, 2, 1, _ => CpuBound());
        var a = Assert.Single(acts).a;
        Assert.Equal(TunerRule.CpuBound, a.Rule);
        Assert.Equal(TunerActionType.Apply, a.Type);
        Assert.True(a.Effects.HasFlag(TunerEffect.HighPriority | TunerEffect.PCoreAffinity | TunerEffect.HoldBackBackground));
    }

    [Fact]
    public void An_interruption_resets_the_three_second_hold()
    {
        var t = new ClosedLoopTuner();
        var acts = Run(t, 0, 10, i => i % 3 == 2 ? S() : CpuBound()); // never 3 in a row
        Assert.Empty(acts);
    }

    [Fact]
    public void Ratio_just_under_the_trigger_does_nothing()
    {
        var t = new ClosedLoopTuner();
        Assert.Empty(Run(t, 0, 30, _ => S(cpu: 9.1, gpu: 8))); // 1.1375
    }

    [Fact]
    public void Cpu_bound_releases_after_15_seconds_below_1_05_not_before()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => CpuBound());
        Assert.Empty(Run(t, 3, 15, _ => S())); // 15 buckets: 14 s elapsed
        var acts = Run(t, 18, 1, _ => S());
        var a = Assert.Single(acts).a;
        Assert.Equal(TunerActionType.Release, a.Type);
        Assert.True(a.Effects.HasFlag(TunerEffect.HighPriority));
    }

    [Fact]
    public void A_ratio_between_1_05_and_1_15_neither_fires_nor_releases()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => CpuBound());
        Assert.Empty(Run(t, 3, 60, _ => S(cpu: 8.8, gpu: 8))); // 1.10
    }

    [Fact]
    public void Cooldown_blocks_a_second_firing_for_60_seconds()
    {
        var t = new ClosedLoopTuner();
        var acts = Run(t, 0, 200, i => i < 3 ? CpuBound() : i < 25 ? S() : CpuBound());
        var applies = acts.Where(x => x.a.Type == TunerActionType.Apply).Select(x => x.sec).ToList();
        Assert.Equal(2, applies.Count);
        Assert.True(applies[1] - applies[0] >= 60);
    }

    [Fact]
    public void Gpu_bound_raises_the_power_profile_and_advises_on_hags()
    {
        var t = new ClosedLoopTuner();
        var a = Assert.Single(Run(t, 0, 3, _ => GpuBound())).a;
        Assert.Equal(TunerRule.GpuBound, a.Rule);
        Assert.Equal(TunerActionType.Apply, a.Type);
        Assert.True(a.Effects.HasFlag(TunerEffect.RaisePowerProfile));
        Assert.True(a.Effects.HasFlag(TunerEffect.AdviseHags));
    }

    [Fact]
    public void Gpu_bound_is_advice_only_while_auto_tdp_is_on()
    {
        var t = new ClosedLoopTuner();
        var a = Assert.Single(Run(t, 0, 3, _ => GpuBound(Plain with { AutoTdpOn = true }))).a;
        Assert.Equal(TunerActionType.Advise, a.Type);
        Assert.False(a.ChangesSystem);
        Assert.False(a.Effects.HasFlag(TunerEffect.RaisePowerProfile));
    }

    [Fact]
    public void Gpu_bound_without_a_power_profile_only_advises()
    {
        var t = new ClosedLoopTuner();
        var a = Assert.Single(Run(t, 0, 3, _ => GpuBound(Plain with { HasPowerProfile = false }))).a;
        Assert.False(a.ChangesSystem);
    }

    [Fact]
    public void Missing_busy_metrics_do_nothing_say_so_once_and_leave_rule_3_working()
    {
        var t = new ClosedLoopTuner();
        var acts = Run(t, 0, 10, _ => S(cpu: null, gpu: null));
        var note = Assert.Single(acts).a;
        Assert.Equal(TunerActionType.Note, note.Type);
        Assert.Null(note.Rule);

        var stutter = Run(t, 10, 3, _ => S(avg: 60, low: 30, cpu: null, gpu: null));
        Assert.Contains(stutter, x => x.a.Rule == TunerRule.MicroStutter && x.a.Type == TunerActionType.Apply);
    }

    [Fact]
    public void Micro_stutter_caps_at_monitor_hz_minus_3_and_trims_memory()
    {
        var t = new ClosedLoopTuner();
        var a = Assert.Single(Run(t, 0, 3, _ => S(avg: 60, low: 40))).a; // 66 %
        Assert.Equal(TunerRule.MicroStutter, a.Rule);
        Assert.Equal(57, a.FrameCapFps);
        Assert.True(a.Effects.HasFlag(TunerEffect.FrameCap | TunerEffect.TrimWorkingSets));
    }

    [Fact]
    public void Micro_stutter_releases_only_after_30_seconds_at_80_percent_or_better()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => S(avg: 60, low: 40));
        Assert.Empty(Run(t, 3, 30, _ => S(avg: 60, low: 48))); // 80 %: 29 s elapsed
        var a = Assert.Single(Run(t, 33, 1, _ => S(avg: 60, low: 48))).a;
        Assert.Equal(TunerActionType.Release, a.Type);
    }

    [Fact]
    public void Thermal_fires_at_over_85_and_goes_to_stock_profile_with_a_toast()
    {
        var t = new ClosedLoopTuner();
        var a = Assert.Single(Run(t, 0, 3, _ => S(temp: 90))).a;
        Assert.Equal(TunerRule.Thermal, a.Rule);
        Assert.True(a.Effects.HasFlag(TunerEffect.StockPowerProfile | TunerEffect.Toast));
        Assert.Empty(Run(t, 3, 1, _ => S(temp: 85))); // 85 is not over 85
    }

    [Fact]
    public void Thermal_releases_below_80_for_30_seconds()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => S(temp: 90));
        Assert.Empty(Run(t, 3, 30, _ => S(temp: 79))); // 29 s elapsed
        Assert.Equal(TunerActionType.Release, Assert.Single(Run(t, 33, 1, _ => S(temp: 79))).a.Type);
    }

    [Fact]
    public void Thermal_defers_to_the_thermal_guard_with_a_toast_only()
    {
        foreach (var ctx in new[] { Plain with { AutoTdpOn = true }, Plain with { ThermalGuardActing = true } })
        {
            var t = new ClosedLoopTuner();
            var a = Assert.Single(Run(t, 0, 3, _ => S(temp: 92, ctx: ctx))).a;
            Assert.Equal(TunerEffect.Toast, a.Effects);
            Assert.False(a.ChangesSystem);
        }
    }

    [Fact]
    public void Thermal_beats_every_other_rule()
    {
        var t = new ClosedLoopTuner();
        // CPU-bound and stuttering and hot all at once: only thermal acts.
        var acts = Run(t, 0, 20, _ => S(avg: 60, low: 30, cpu: 12, gpu: 8, temp: 92));
        Assert.All(acts, x => Assert.Equal(TunerRule.Thermal, x.a.Rule));
        Assert.Single(acts);
    }

    [Fact]
    public void Other_rules_start_counting_afresh_after_thermal_clears()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => S(temp: 92));
        // Thermal releases at 33 s; CPU-bound was held back until then, so it needs its own 3 s from there.
        var acts = Run(t, 3, 40, _ => S(temp: 70, cpu: 12, gpu: 8));
        Assert.Equal(33, Assert.Single(acts, x => x.a.Type == TunerActionType.Release).sec);
        Assert.Equal(35, Assert.Single(acts, x => x.a.Rule == TunerRule.CpuBound && x.a.Type == TunerActionType.Apply).sec);
    }

    [Fact]
    public void Anti_cheat_games_get_no_priority_affinity_or_memory_actions()
    {
        var ac = Plain with { AntiCheat = true };

        var cpu = Assert.Single(Run(new ClosedLoopTuner(), 0, 3, _ => CpuBound(ac))).a;
        Assert.Equal(TunerEffect.None, cpu.Effects);
        Assert.Equal(TunerActionType.Advise, cpu.Type);

        var stutter = Assert.Single(Run(new ClosedLoopTuner(), 0, 3, _ => S(avg: 60, low: 30, ctx: ac))).a;
        Assert.False(stutter.Effects.HasFlag(TunerEffect.TrimWorkingSets));
        Assert.True(stutter.Effects.HasFlag(TunerEffect.FrameCap)); // an RTSS cap is not a game-process change
    }

    [Fact]
    public void Max_three_applications_per_rule_then_it_stops_with_one_note()
    {
        var t = new ClosedLoopTuner();
        // Cycle: 3 s CPU-bound, 20 s fine (release), cooldown 60 s, repeat. Steady 1% low so nothing is judged worse.
        var acts = Run(t, 0, 1500, i => i % 100 < 3 ? CpuBound() : S());
        Assert.Equal(3, acts.Count(x => x.a.Rule == TunerRule.CpuBound && x.a.Type == TunerActionType.Apply));
        Assert.Single(acts, x => x.a.Type == TunerActionType.Note && x.a.Rule == TunerRule.CpuBound);
    }

    [Fact]
    public void A_change_that_made_1_percent_low_worse_is_undone_and_never_retried()
    {
        var t = new ClosedLoopTuner();
        var acts = Run(t, 0, 400, i => i < 3 ? S(cpu: 12, gpu: 8, low: 50) : S(cpu: 12, gpu: 8, low: 30));
        var undo = Assert.Single(acts, x => x.a.Type == TunerActionType.Undo);
        Assert.Equal(TunerRule.CpuBound, undo.a.Rule);
        Assert.InRange(undo.sec, 2 + 16, 2 + 18);
        Assert.Single(acts, x => x.a.Type == TunerActionType.Apply && x.a.Rule == TunerRule.CpuBound); // no retry
    }

    [Fact]
    public void A_change_that_did_not_hurt_is_kept()
    {
        var t = new ClosedLoopTuner();
        var acts = Run(t, 0, 60, i => S(cpu: 12, gpu: 8, low: 50 + (i < 3 ? 0 : 5)));
        Assert.DoesNotContain(acts, x => x.a.Type == TunerActionType.Undo);
    }

    [Fact]
    public void An_active_rule_is_put_back_if_its_input_vanishes()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => CpuBound());
        var acts = Run(t, 3, 20, _ => S(cpu: null, gpu: null));
        Assert.Contains(acts, x => x.a.Rule == TunerRule.CpuBound && x.a.Type == TunerActionType.Release);

        var s = new ClosedLoopTuner();
        Run(s, 0, 3, _ => S(avg: 60, low: 30));
        var empty = new TunerSample(default, null, null, Plain);
        Assert.Contains(Run(s, 3, 40, _ => empty), x => x.a.Rule == TunerRule.MicroStutter && x.a.Type == TunerActionType.Release);
    }

    [Fact]
    public void Thermal_stays_on_if_the_sensor_vanishes()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => S(temp: 92));
        Assert.Empty(Run(t, 3, 120, _ => S(temp: null)));
    }

    [Fact]
    public void Rules_that_were_already_active_stay_held_while_thermal_is_active()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => CpuBound());
        Run(t, 3, 3, _ => S(temp: 92));
        // Plenty of clear conditions for rule 1, but thermal is active, so no release until it clears.
        var acts = Run(t, 6, 25, _ => S(temp: 92, cpu: 8, gpu: 8));
        Assert.DoesNotContain(acts, x => x.a.Rule == TunerRule.CpuBound);
    }

    [Fact]
    public void Thermal_is_not_undone_while_still_hot_even_if_the_temperature_rose()
    {
        var t = new ClosedLoopTuner();
        var acts = Run(t, 0, 60, i => S(temp: i < 3 ? 88 : 95));
        Assert.DoesNotContain(acts, x => x.a.Type == TunerActionType.Undo);
    }

    [Fact]
    public void Others_wait_while_over_the_limit_even_when_thermal_is_in_cooldown_or_used_up()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => S(temp: 90));            // thermal fires at 2 s
        Run(t, 3, 5, _ => S(temp: 78));            // clear for a few seconds, still active
        // Hot again (over 85) with CPU-bound: thermal is active, so held.
        Assert.Empty(Run(t, 8, 10, _ => S(temp: 90, cpu: 12, gpu: 8)));

        // Fully released, then in cooldown (20 s) and hot again: others must still wait.
        var u = new ClosedLoopTuner();
        Run(u, 0, 3, _ => S(temp: 90));
        Run(u, 3, 31, _ => S(temp: 70));           // releases at 33 s
        var acts = Run(u, 34, 10, _ => S(temp: 90, cpu: 12, gpu: 8)); // cooldown to 53 s
        Assert.DoesNotContain(acts, x => x.a.Rule is TunerRule.CpuBound or TunerRule.GpuBound or TunerRule.MicroStutter);
    }

    [Fact]
    public void Status_shows_active_cooldown_and_application_count()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => CpuBound()); // fires at 2 s
        var at = T0.AddSeconds(10);
        var cpu = t.Status(at).Single(x => x.Rule == TunerRule.CpuBound);
        Assert.True(cpu.Active);
        Assert.Equal(1, cpu.Applications);
        Assert.Equal(TimeSpan.Zero, cpu.CooldownLeft); // active rules report no cooldown
        Assert.All(t.Status(at).Where(x => x.Rule != TunerRule.CpuBound), x => Assert.False(x.Active));

        Run(t, 3, 16, _ => S()); // released at 18 s, cooldown 60 s
        var after = t.Status(T0.AddSeconds(30)).Single(x => x.Rule == TunerRule.CpuBound);
        Assert.False(after.Active);
        Assert.InRange(after.CooldownLeft.TotalSeconds, 40, 50);
    }

    [Fact]
    public void Why_not_running_explains_each_blocker_in_order()
    {
        Assert.Contains("switched off", ClosedLoopAdvice.WhyNotRunning(ClosedLoopMode.Off, true, null, true, true));
        Assert.Contains("No game", ClosedLoopAdvice.WhyNotRunning(ClosedLoopMode.On, false, null, true, true));
        Assert.Equal("needs admin", ClosedLoopAdvice.WhyNotRunning(ClosedLoopMode.On, true, "needs admin", true, true));
        Assert.Contains("Waiting for frames", ClosedLoopAdvice.WhyNotRunning(ClosedLoopMode.WatchOnly, true, null, false, true));
        Assert.Contains("busy readings", ClosedLoopAdvice.WhyNotRunning(ClosedLoopMode.On, true, null, true, false));
        Assert.Null(ClosedLoopAdvice.WhyNotRunning(ClosedLoopMode.On, true, null, true, true));
    }

    [Fact]
    public void Ending_the_session_releases_whatever_is_active_once()
    {
        var t = new ClosedLoopTuner();
        Run(t, 0, 3, _ => CpuBound());
        var end = t.EndSession();
        var a = Assert.Single(end);
        Assert.Equal(TunerActionType.Release, a.Type);
        Assert.Equal(TunerRule.CpuBound, a.Rule);
        Assert.Empty(t.EndSession());
    }

    [Fact]
    public void Ten_minute_flapping_run_never_toggles_rapidly()
    {
        var t = new ClosedLoopTuner();
        // Ratio flips between CPU-bound and fine every 4 s; temperature drifts across the limits; 1% low wobbles.
        var acts = Run(t, 0, 600, i => S(
            avg: 60, low: i % 7 < 3 ? 35 : 55,
            cpu: i % 8 < 4 ? 12 : 7, gpu: 8,
            temp: 70 + 18 * Math.Sin(i / 40.0)));

        foreach (var rule in Enum.GetValues<TunerRule>())
        {
            var applies = acts.Where(x => x.a.Rule == rule && x.a.Type is TunerActionType.Apply or TunerActionType.Advise).Select(x => x.sec).ToList();
            var real = acts.Where(x => x.a.Rule == rule && x.a.Type == TunerActionType.Apply).Select(x => x.sec).ToList();
            Assert.True(real.Count <= ClosedLoopTuner.MaxApplicationsPerRule, $"{rule} applied {real.Count} times");
            var min = rule switch
            {
                TunerRule.MicroStutter => ClosedLoopTuner.StutterCooldown,
                TunerRule.Thermal => ClosedLoopTuner.ThermalCooldown,
                _ => ClosedLoopTuner.BoundCooldown,
            };
            for (var i = 1; i < applies.Count; i++)
                Assert.True(applies[i] - applies[i - 1] >= min.TotalSeconds, $"{rule} fired again after {applies[i] - applies[i - 1]} s");
        }
    }

    [Fact]
    public void No_frames_and_no_temperatures_means_no_decisions()
    {
        var t = new ClosedLoopTuner();
        var empty = new TunerSample(default, null, null, Plain);
        Assert.Empty(Run(t, 0, 30, _ => empty));
    }
}
