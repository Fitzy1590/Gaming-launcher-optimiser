using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class OverlayTests
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
    public void Includes141Target()
    {
        Assert.Contains(141, LauncherSettings.FpsTargets);
    }
}
