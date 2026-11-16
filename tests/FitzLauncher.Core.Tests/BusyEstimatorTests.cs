using FitzLauncher.Core.Telemetry;

namespace FitzLauncher.Core.Tests;

public class BusyEstimatorTests
{
    [Fact]
    public void LowGpuLoadBelowPaceReadsAsCpuBound()
    {
        var (cpu, gpu) = BusyEstimator.Estimate(70, 60, 0, 144)!.Value;
        Assert.True(cpu / gpu > ClosedLoopTuner.BoundRatio);
    }

    [Fact]
    public void FullGpuLoadReadsAsGpuBound()
    {
        var (cpu, gpu) = BusyEstimator.Estimate(70, 99, 0, 144)!.Value;
        Assert.True(cpu / gpu < ClosedLoopTuner.BoundReleaseRatio);
    }

    [Theory]
    [InlineData(59, 60, 144)]  // at the frame cap
    [InlineData(140, 0, 144)]  // at the refresh rate
    public void AtPaceCantTell(double fps, int cap, int hz) =>
        Assert.Null(BusyEstimator.Estimate(fps, 40, cap, hz));

    [Theory]
    [InlineData(0.0, 50.0)]
    [InlineData(60.0, null)]
    [InlineData(60.0, 2.0)]
    public void MissingOrTinyReadingsCantTell(double fps, double? gpu) =>
        Assert.Null(BusyEstimator.Estimate(fps, gpu, 0, 0));
}
