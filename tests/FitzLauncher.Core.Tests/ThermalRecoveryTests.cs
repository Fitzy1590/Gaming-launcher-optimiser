using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Core.Tests;

public class ThermalRecoveryTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);

    private static ThermalGovernor Throttled()
    {
        var g = new ThermalGovernor(110, new ThermalGuardOptions(), triggerC: 85);
        g.Update(90, 70, T0);
        Assert.Equal(100, g.Update(90, 70, T0.AddSeconds(16)));   // sustained heat: one step down
        return g;
    }

    [Fact]
    public void WhenTheChipIsClearlyCoolEachStepComesBackAfterFifteenSeconds()
    {
        var g = Throttled();
        Assert.Null(g.Update(65, 60, T0.AddSeconds(20)));          // starts the cool timer
        Assert.Null(g.Update(65, 60, T0.AddSeconds(30)));
        Assert.Equal(110, g.Update(65, 60, T0.AddSeconds(36)));    // 16 s later (a lowered cap used to wait 45 s)
        Assert.False(g.IsThrottling);
    }

    [Fact]
    public void WhenOnlyJustBelowTheReleasePointItStillWaitsTheFullFortyFiveSeconds()
    {
        var g = Throttled();
        Assert.Null(g.Update(79, 70, T0.AddSeconds(20)));          // below 80 (trigger 85 - 5) but not clearly cool
        Assert.Null(g.Update(79, 70, T0.AddSeconds(40)));
        Assert.Null(g.Update(79, 70, T0.AddSeconds(60)));
        Assert.Equal(110, g.Update(79, 70, T0.AddSeconds(66)));    // 46 s after the timer started
    }
}
