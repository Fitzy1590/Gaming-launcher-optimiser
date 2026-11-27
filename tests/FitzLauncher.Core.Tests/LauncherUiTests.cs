using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Telemetry;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class PerformanceProfilesTests
{
    [Theory]
    [InlineData(PerformanceProfile.Turbo, CoolingMode.Off, AutoTdpMode.Off)]
    [InlineData(PerformanceProfile.BatterySaver, CoolingMode.Coolest, AutoTdpMode.On)]
    [InlineData(PerformanceProfile.Balanced, CoolingMode.Balanced, null)]
    public void Profiles_write_only_existing_settings(PerformanceProfile p, CoolingMode cooling, AutoTdpMode? autoTdp) =>
        Assert.Equal((cooling, autoTdp), PerformanceProfiles.For(p));

    [Fact]
    public void Every_profile_round_trips_through_match()
    {
        foreach (var p in Enum.GetValues<PerformanceProfile>())
        {
            var (c, a) = PerformanceProfiles.For(p);
            Assert.Equal(p, PerformanceProfiles.Match(c, CoolingMode.Off, a, managesPower: true));
        }
    }

    [Fact]
    public void Default_cooling_counts_when_the_game_has_none() =>
        Assert.Equal(PerformanceProfile.Balanced, PerformanceProfiles.Match(null, CoolingMode.Balanced, null, managesPower: true));

    [Fact]
    public void Mixed_settings_are_custom() =>
        Assert.Null(PerformanceProfiles.Match(CoolingMode.Off, CoolingMode.Balanced, AutoTdpMode.On, managesPower: true));

    [Fact]
    public void Auto_tdp_is_ignored_without_power_management() =>
        Assert.Equal(PerformanceProfile.Turbo, PerformanceProfiles.Match(CoolingMode.Off, CoolingMode.Balanced, AutoTdpMode.On, managesPower: false));

    [Fact]
    public void Cool_mode_matches_no_profile() =>
        Assert.Null(PerformanceProfiles.Match(CoolingMode.Cool, CoolingMode.Balanced, null, managesPower: false));
}

public class LiveReadoutTests
{
    [Fact]
    public void Polls_only_when_front_idle_and_on_the_library() =>
        Assert.True(LiveReadout.ShouldPoll(lowImpact: false, gameRunning: false, windowActive: true, libraryVisible: true, safeMode: false));

    [Theory]
    [InlineData(true, false, true, true, false)]   // low-impact mode
    [InlineData(false, true, true, true, false)]   // a game is running
    [InlineData(false, false, false, true, false)] // another window (a game) is in front
    [InlineData(false, false, true, false, false)] // another page
    [InlineData(false, false, true, true, true)]   // safe mode
    public void Never_polls_otherwise(bool lowImpact, bool gameRunning, bool active, bool library, bool safe) =>
        Assert.False(LiveReadout.ShouldPoll(lowImpact, gameRunning, active, library, safe));

    [Fact]
    public void Paused_reason_names_the_cause()
    {
        Assert.Contains("play", LiveReadout.PausedReason(false, true, false));
        Assert.Contains("Low-impact", LiveReadout.PausedReason(true, true, false));
        Assert.Contains("safe mode", LiveReadout.PausedReason(true, true, true));
        Assert.Equal("", LiveReadout.PausedReason(false, false, false));
    }

    [Fact]
    public void Formats_readings()
    {
        Assert.Equal("3.8 GHz", LiveReadout.Clock(3812));
        Assert.Equal("–", LiveReadout.Clock(null));
        Assert.Equal("–", LiveReadout.Clock(0));
        Assert.Equal("72%", LiveReadout.Percent(71.6));
        Assert.Equal("100%", LiveReadout.Percent(140));
        Assert.Equal("38°C", LiveReadout.Temp(38.2));
        Assert.Equal("", LiveReadout.Temp(null));
        Assert.Equal("11.5 / 16 GB", LiveReadout.Memory(16384, 4608));
        Assert.Equal("–", LiveReadout.Memory(0, 0));
        Assert.Equal(75, LiveReadout.MemoryPercent(16384, 4096));
        Assert.Equal(0, LiveReadout.Meter(null));
    }
}
