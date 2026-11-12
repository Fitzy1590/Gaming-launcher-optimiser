using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class SmoothModeTests
{
    private static SessionSummary Last(double low, int target = 100, double? cpuTemp = null) => new()
    {
        HasFpsData = true,
        AverageFps = target,
        OnePercentLowFps = low,
        TargetFps = target,
        MaxCpuTempC = cpuTemp,
        Start = DateTimeOffset.Now.AddMinutes(-30),
        End = DateTimeOffset.Now,
    };

    private static (Game g, LauncherSettings s) Fresh() => (new Game { FpsTarget = 100 }, new LauncherSettings());

    [Fact]
    public void LowCapIsSuggestedOnlyWhenThe1PercentLowMissedTheTarget()
    {
        var (g, _) = Fresh();
        Assert.Null(SmoothMode.CapTarget(g, Last(low: 95)));
        Assert.True(SmoothMode.CapTarget(g, Last(low: 60)) < 100);
        Assert.Null(SmoothMode.CapTarget(g, null));
    }

    [Fact]
    public void TurningItOnAppliesTheChangesAndOffPutsEverythingBack()
    {
        var (g, s) = Fresh();
        g.AutoTdp = AutoTdpMode.Observe;
        g.Cooling = CoolingMode.Balanced;
        s.BackgroundLimiter = false;
        var last = Last(low: 60, cpuTemp: 95);

        SmoothMode.SetEnabled(g, s, true, last, autoTdpAvailable: true);

        Assert.True(g.SmoothMode);
        Assert.True(g.FpsTarget < 100);
        Assert.Equal(AutoTdpMode.On, g.AutoTdp);
        Assert.Equal(CoolingMode.Cool, g.Cooling);
        Assert.True(s.BackgroundLimiter);
        Assert.True(s.ResourceGuard.Enabled);

        SmoothMode.SetEnabled(g, s, false, last, autoTdpAvailable: true);

        Assert.False(g.SmoothMode);
        Assert.Equal(100, g.FpsTarget);
        Assert.Equal(AutoTdpMode.Observe, g.AutoTdp);
        Assert.Equal(CoolingMode.Balanced, g.Cooling);
        Assert.False(s.BackgroundLimiter);
        Assert.False(s.ResourceGuard.Enabled);
        Assert.Empty(g.SmoothUndo);
    }

    [Fact]
    public void UndoRestoresAnUnsetValueAsUnsetAndDoesNothingForAChangeItDidNotMake()
    {
        var (g, s) = Fresh();
        Assert.Null(g.AutoTdp);
        SmoothMode.Apply(g, s, SmoothFix.AutoTdpOn, null);
        Assert.Equal(AutoTdpMode.On, g.AutoTdp);
        SmoothMode.Undo(g, s, SmoothFix.AutoTdpOn);
        Assert.Null(g.AutoTdp);
        Assert.Null(SmoothMode.Undo(g, s, SmoothFix.CoolMode));
    }

    [Fact]
    public void AppliedChangesStayListedSoTheyCanBeUndoneAndNothingIsSuggestedTwice()
    {
        var (g, s) = Fresh();
        SmoothMode.Apply(g, s, SmoothFix.ResourceGuard, null);
        Assert.Null(SmoothMode.Apply(g, s, SmoothFix.ResourceGuard, null)); // second apply is a no-op
        var item = Assert.Single(SmoothMode.Suggest(g, s, null, autoTdpAvailable: false), x => x.Fix == SmoothFix.ResourceGuard);
        Assert.True(item.Applied);
    }

    [Fact]
    public void AutoTdpIsNotSuggestedWhereItCannotRun()
    {
        var (g, s) = Fresh();
        Assert.DoesNotContain(SmoothMode.Suggest(g, s, null, autoTdpAvailable: false), x => x.Fix == SmoothFix.AutoTdpOn);
    }

    [Fact]
    public void TheWindowsSecurityNoteIsAdviceOnlyAndAppearsOnlyWhenStuttersAreFrequent()
    {
        var calm = Last(low: 95);
        Assert.DoesNotContain(SmoothMode.Advice(calm), a => a.Contains("Memory Integrity"));
        var rough = Last(low: 50);
        rough.StutterCount = 120;
        Assert.Contains(SmoothMode.Advice(rough), a => a.Contains("Memory Integrity") && a.Contains("never changes it"));
    }
}
