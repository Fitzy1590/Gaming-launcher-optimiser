using FitzLauncher.Core.Models;
using FitzLauncher.Core.Upscalers;

namespace FitzLauncher.Core.Tests;

public class AmdNrSupportTests
{
    private static GpuInfo Amd(string name) => new() { Name = name, Vendor = GpuVendor.Amd };

    [Theory]
    [InlineData("AMD Radeon RX 9070 XT")]
    [InlineData("AMD Radeon RX 7800 XT")]
    [InlineData("AMD Radeon RX 7600M")]
    public void Rx7000And9000_Supported(string name) =>
        Assert.Equal(AmdNrLevel.Supported, AmdNrSupport.Check(Amd(name), false).Level);

    [Theory]
    [InlineData("AMD Radeon 780M Graphics")]
    [InlineData("AMD Radeon 890M Graphics")]
    [InlineData("AMD Radeon 880M Graphics")]
    [InlineData("AMD Radeon(TM) 8060S Graphics")]
    public void HandheldAndStrixHalo_Experimental(string name)
    {
        var r = AmdNrSupport.Check(Amd(name), false);
        Assert.Equal(AmdNrLevel.Experimental, r.Level);
        Assert.True(r.CanOffer);
    }

    [Theory]
    [InlineData("AMD Radeon 760M Graphics")]
    [InlineData("AMD Radeon 740M Graphics")]
    [InlineData("AMD Radeon 860M Graphics")]
    [InlineData("AMD Radeon 840M Graphics")]
    [InlineData("AMD Radeon RX 6800 XT")]
    [InlineData("AMD Custom GPU 0405")]
    public void SmallOldOrUnknownAmd_NotOffered(string name) =>
        Assert.False(AmdNrSupport.Check(Amd(name), false).CanOffer);

    [Fact]
    public void NonAmdOrUnknown_NotOffered()
    {
        Assert.False(AmdNrSupport.Check(new GpuInfo { Name = "RTX 4070", Vendor = GpuVendor.Nvidia }, false).CanOffer);
        Assert.False(AmdNrSupport.Check(null, false).CanOffer);
    }

    [Fact]
    public void AntiCheat_AlwaysRefused()
    {
        var r = AmdNrSupport.Check(Amd("AMD Radeon RX 9070 XT"), true);
        Assert.Equal(AmdNrLevel.NotAvailable, r.Level);
        Assert.Contains("anti-cheat", r.Reason);
    }

    [Fact]
    public void Settings_AmdNrAddsOnlyEnabledAndBackend_AndOffByDefault()
    {
        Assert.DoesNotContain(OptiScalerSettings.For(GpuFamily.AmdRdna4, null), x => x.Section == "DlssNr");
        var s = OptiScalerSettings.For(GpuFamily.AmdRdna4, null, amdNr: true);
        Assert.Contains(("DlssNr", "Enabled", "true"), s);
        Assert.Contains(("DlssNr", "NrBackend", "lmxxf"), s);
        Assert.Equal(2, s.Count(x => x.Section == "DlssNr"));
    }
}
