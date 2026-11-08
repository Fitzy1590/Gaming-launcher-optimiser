using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class OverlayAndStabiliserTests
{
    private static readonly OverlayValues V = new(117.4, 98, 45.2, 77, 75, 96, 117);

    [Fact]
    public void DefaultRowIsFpsWattsTemps()
    {
        var o = new OverlaySettings { Coloured = false };
        Assert.Equal("117 FPS   45 W   CPU 77°   GPU 75°", OverlayFormatter.Format(o, V));
    }

    [Fact]
    public void ColumnPutsEachOnItsOwnLine()
    {
        var o = new OverlaySettings { Coloured = false, Arrangement = OverlayArrangement.Column, CpuTemp = false };
        Assert.Equal("117 FPS\n45 W\nGPU 75°", OverlayFormatter.Format(o, V));
    }

    [Fact]
    public void ColouredUsesRtssTagsAndSize()
    {
        var o = new OverlaySettings { Watts = false, CpuTemp = false, GpuTemp = false, SizePercent = 80 };
        Assert.Equal("<S=80><C=FFFFFF>117<C> <C=9AA4B2>FPS<C><S>", OverlayFormatter.Format(o, V));
    }

    [Fact]
    public void LegacyStylesMap()
    {
        Assert.False(OverlaySettings.FromLegacy(true, OverlayStyle.Off).Enabled);
        Assert.False(OverlaySettings.FromLegacy(false, OverlayStyle.Detailed).Enabled);
        var d = OverlaySettings.FromLegacy(true, OverlayStyle.Detailed);
        Assert.True(d.Enabled && d.Cap && d.OnePercentLow);
    }

    [Fact]
    public void ExtrasAppearOnlyWhenRelevant()
    {
        var o = new OverlaySettings { Coloured = false, Watts = false, CpuTemp = false, GpuTemp = false };
        Assert.Equal("117 FPS   cooling: cap 110", OverlayFormatter.Format(o, V with { ThermalThrottling = true, Cap = 110 }));
    }

    [Fact]
    public void StabiliserDropsToAHoldableRungAndClimbsBack()
    {
        var s = new FpsStabiliser(117, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30));
        var t = new DateTime(2026, 1, 1);
        int? change = null;
        // Heavy area: dips to ~95.
        for (var i = 0; i < 10 && change is null; i++) change = s.Update(i % 3 == 0 ? 104 : 95, t = t.AddSeconds(2));
        Assert.Equal(90, change);
        Assert.True(s.Lowered);

        // Holds 90 easily with the GPU not maxed: steps up after the hold time, one rung.
        change = null;
        for (var i = 0; i < 30 && change is null; i++) change = s.Update(90, t = t.AddSeconds(2), gpuLoad: 70);
        Assert.Equal(100, change);
    }

    [Fact]
    public void StabiliserStaysPutWhenTheGameKeepsUp()
    {
        var s = new FpsStabiliser(117);
        var t = DateTime.UtcNow;
        for (var i = 0; i < 40; i++) Assert.Null(s.Update(116.5, t = t.AddSeconds(2), 95));
        Assert.Equal(117, s.Current);
    }

    [Fact]
    public void StabiliserIgnoresPausesAndRespectsGpuLimit()
    {
        var s = new FpsStabiliser(141);
        var t = DateTime.UtcNow;
        Assert.Null(s.Update(0, t));
        s.SetCeiling(141);
        int? change = null;
        for (var i = 0; i < 10 && change is null; i++) change = s.Update(112, t = t.AddSeconds(2));
        Assert.Equal(110, change);
        // At 110 with the GPU at 99%: no step up.
        for (var i = 0; i < 40; i++) Assert.Null(s.Update(110, t = t.AddSeconds(2), 99));
    }

    [Fact]
    public void Includes141Target()
    {
        Assert.Contains(141, LauncherSettings.FpsTargets);
    }
}
