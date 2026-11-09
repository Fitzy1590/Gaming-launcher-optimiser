using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class BaselineAndPowerTests
{
    private static SessionSummary S(double avg, double low, int scale, int? watts, string key = "1920x1200@120", int target = 117, double? gpu = 95, int daysAgo = 1) => new()
    {
        HasFpsData = true, AverageFps = avg, OnePercentLowFps = low, RenderScale = scale, PowerWatts = watts, DisplayKey = key,
        DisplayWidth = 1920, DisplayHeight = 1200, TargetFps = target, AvgGpuLoad = gpu,
        Start = DateTimeOffset.Now.AddDays(-daysAgo).AddHours(-1), End = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    [Fact]
    public void BaselinePrefersSharpestThenLowestPowerOnThisScreen()
    {
        var history = new[]
        {
            S(116, 100, 83, 30), S(115, 98, 100, 45), S(116, 101, 100, 38), // two at 100 %: 38 W wins
            S(90, 70, 100, 25),                                               // missed: ignored
            S(117, 110, 100, 20, key: "3440x1440@144"),                       // other screen
        };
        var b = BaselinePicker.Pick(history, 117, "1920x1200@120")!;
        Assert.Equal(100, b.RenderScale);
        Assert.Equal(38, b.Watts);
        Assert.Contains("1920×1200", b.Describe());
    }

    [Fact]
    public void NoBaselineWhenNothingHeldTheTarget()
    {
        Assert.Null(BaselinePicker.Pick([S(80, 60, 100, 30)], 117, "1920x1200@120"));
    }

    [Fact]
    public void GpuBoundAdviceSuggestsALowerResolution()
    {
        var a = GraphicsAdvisor.Advise(S(80, 65, 100, 30, gpu: 99), 117, 1920, 1200)!;
        Assert.StartsWith("GPU-limited", a.Headline);
        Assert.NotNull(a.Width);
        Assert.True(a.Width < 1920 && a.Height < 1200);
        Assert.Equal(0, a.Width % 8);
    }

    [Fact]
    public void CpuBoundAdviceSaysResolutionWontHelp()
    {
        var a = GraphicsAdvisor.Advise(S(80, 60, 100, 30, gpu: 55), 117, 1920, 1200)!;
        Assert.StartsWith("CPU-limited", a.Headline);
        Assert.Null(a.Width);
    }

    [Fact]
    public void OnTargetAdviceMentionsSpareRoom()
    {
        var a = GraphicsAdvisor.Advise(S(116.5, 104, 100, 30, gpu: 60), 117, 1920, 1200)!;
        Assert.Contains("holding", a.Headline);
        Assert.Single(a.Steps);
    }

    [Fact]
    public void GovernorRaisesOnMissesAndFindsTheLowestHoldingPower()
    {
        var g = new PowerGovernor(30, 15, 60, 117);
        var t = new DateTime(2026, 1, 1);
        int? change = null;
        for (var i = 0; i < 20 && change is null; i++) change = g.Update(100, t = t.AddSeconds(2));
        Assert.Equal(33, change);

        // Holds at 33: steps down after 40 s, but never back to 30 (which missed).
        change = null;
        for (var i = 0; i < 40 && change is null; i++) change = g.Update(117, t = t.AddSeconds(2));
        Assert.Equal(31, change);
        Assert.Equal(31, g.Current);
        for (var i = 0; i < 60; i++) Assert.Null(g.Update(117, t = t.AddSeconds(2)));
        Assert.Equal(31, g.Learned);
    }

    [Fact]
    public void EstimateScalesFromANeighbouringTarget()
    {
        var learned = new Dictionary<int, int> { [90] = 20 };
        Assert.Equal(20, PowerGovernor.Estimate(learned, 90, 65));
        Assert.InRange(PowerGovernor.Estimate(learned, 117, 65), 27, 29);
        Assert.Equal(65, PowerGovernor.Estimate(new Dictionary<int, int>(), 117, 65));
    }
}

public class DisplayModeTests
{
    private static HardwareProfile Hw(bool win11) => new() { OsBuild = win11 ? 26100 : 19045 };

    [Theory]
    [InlineData(true, false, false, false, true, true)]   // DX12 → borderless
    [InlineData(false, true, false, false, false, true)]  // Vulkan on Win10 → borderless
    [InlineData(false, false, true, false, true, false)]  // DX9 → fullscreen
    [InlineData(false, false, false, true, true, false)]  // OpenGL → fullscreen
    [InlineData(false, false, false, false, true, true)]  // DX11 on Win11 → borderless
    [InlineData(false, false, false, false, false, false)]// DX11 on Win10 → fullscreen
    public void RecommendsByApi(bool dx12, bool vulkan, bool dx9, bool gl, bool win11, bool borderless)
    {
        var scan = new GameScanResult { UsesDx12 = dx12, UsesVulkan = vulkan, UsesDx9 = dx9, UsesOpenGl = gl };
        Assert.Equal(borderless, OptimizationPlanner.RecommendDisplayMode(scan, Hw(win11)).Borderless);
    }
}
