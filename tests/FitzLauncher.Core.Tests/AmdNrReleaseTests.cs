using FitzLauncher.Core.Emulation;
using FitzLauncher.Core.Upscalers;

namespace FitzLauncher.Core.Tests;

public class AmdNrReleaseTests
{
    private const string Base = "https://github.com/3zwr1/AMD-NR---OptiScaler/releases/download/";
    private static readonly string Hash = new('a', 64);

    private static EmuRelease Rel(string tag, int day, params EmuAsset[] assets) =>
        new(tag, tag, new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero), true, [.. assets]);

    private static EmuAsset Zip(string tag, string name, string? sha = null, long size = 526_000_000, string? url = null) =>
        new(name, url ?? $"{Base}{tag}/{name}", size, sha ?? Hash);

    [Fact]
    public void Pick_TakesNewestPrereleaseMainZip_NotLauncherMochiOrRuntime()
    {
        var r23 = Rel("Alpha0.3.5.8R23", 14,
            Zip("Alpha0.3.5.8R23", "AMDNR-Launcher-0.3.5.108.exe", size: 71_000_000),
            Zip("Alpha0.3.5.8R23", "AMDNR-Mochi-0.3.5.8R23-component.zip", size: 14_000_000),
            Zip("Alpha0.3.5.8R23", "v0.6.0-Runtime.zip"),
            Zip("Alpha0.3.5.8R23", "AMDNR-v0.3.5.8R23.zip"));
        var r22 = Rel("Alpha0.3.5.8R22", 13, Zip("Alpha0.3.5.8R22", "AMDNR-v0.3.5.8R22.zip"));
        var (rel, asset) = AmdNrRelease.Pick([r22, r23])!.Value;
        Assert.Equal("Alpha0.3.5.8R23", rel.Tag);
        Assert.Equal("AMDNR-v0.3.5.8R23.zip", asset.Name);
    }

    [Fact]
    public void Pick_SkipsReleaseWithoutDigest_FallingBackToOlderOne()
    {
        var noDigest = Rel("R23", 14, new EmuAsset("AMDNR-v0.3.5.8R23.zip", Base + "R23/AMDNR-v0.3.5.8R23.zip", 5, null));
        var ok = Rel("R22", 13, Zip("R22", "AMDNR-v0.3.5.8R22.zip"));
        Assert.Equal("R22", AmdNrRelease.Pick([noDigest, ok])!.Value.Release.Tag);
        Assert.Null(AmdNrRelease.Pick([noDigest]));
    }

    [Theory]
    [InlineData("https://github.com/someone-else/AMD-NR---OptiScaler/releases/download/R23/AMDNR-v1.zip")]
    [InlineData("https://evil.example.com/3zwr1/AMD-NR---OptiScaler/releases/download/R23/AMDNR-v1.zip")]
    [InlineData("http://github.com/3zwr1/AMD-NR---OptiScaler/releases/download/R23/AMDNR-v1.zip")]
    public void Pick_RefusesOtherOwnersHostsAndPlainHttp(string url) =>
        Assert.Null(AmdNrRelease.Pick([Rel("R23", 14, Zip("R23", "AMDNR-v1.zip", url: url))]));

    [Fact]
    public void Pick_EmptyOrUnrelated_ReturnsNull()
    {
        Assert.Null(AmdNrRelease.Pick([]));
        Assert.Null(AmdNrRelease.Pick([Rel("R1", 1, Zip("R1", "SHA256SUMS.txt"))]));
    }

    [Fact]
    public void FreeSpace_IsTwiceTheZipPlusSlack_AndMissingFilesAreNamed()
    {
        var a = Zip("R23", "AMDNR-v1.zip", size: 526L * 1024 * 1024);
        Assert.Equal(526L * 1024 * 1024 * 2 + 200L * 1024 * 1024, AmdNrRelease.RequiredFreeBytes(a));
        Assert.Equal(["LmxxfNrRuntime.pak"], AmdNrRelease.MissingFiles(f => f != "LmxxfNrRuntime.pak"));
        Assert.Empty(AmdNrRelease.MissingFiles(_ => true));
    }
}

public class AmdNrTweakTests
{
    private static string MakeRelease(string root, params string[] files)
    {
        var d = Path.Combine(root, "amdnr"); Directory.CreateDirectory(d);
        foreach (var f in files) File.WriteAllText(Path.Combine(d, f), "x");
        return d;
    }

    [Fact]
    public void Tweak_AmdNr_NeedsAllThreeFiles_AndMarksLedgerAndKeys()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var partial = MakeRelease(root, "OptiScaler.dll", "LmxxfNrRuntime.dll", "OptiScaler.ini");
            Assert.Null(FitzLauncher.Core.Upscalers.UpscalerAdvisor.OptiScalerTweak(partial, game, _ => false, FitzLauncher.Core.Upscalers.GpuFamily.AmdRdna4, null, amdNr: true));

            File.WriteAllText(Path.Combine(partial, "LmxxfNrRuntime.pak"), "x");
            var t = FitzLauncher.Core.Upscalers.UpscalerAdvisor.OptiScalerTweak(partial, game, _ => false, FitzLauncher.Core.Upscalers.GpuFamily.AmdRdna4, null, amdNr: true)!;
            Assert.All(t.Actions.OfType<FitzLauncher.Core.Models.FileReplaceAction>(), a => Assert.Equal("AMDNR", a.NewVersion));
            Assert.Contains(t.Actions.OfType<FitzLauncher.Core.Models.FileReplaceAction>(), a => a.TargetPath.EndsWith("LmxxfNrRuntime.pak"));
            Assert.Contains(t.Actions.OfType<FitzLauncher.Core.Models.IniSetAction>(), a => a is { Section: "DlssNr", Key: "NrBackend", Value: "lmxxf" });
            Assert.Equal("optiscaler", t.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Status_TellsAmdNrFromStock_AndDetectsAnyInstall()
    {
        var stock = new List<FitzLauncher.Core.Models.AppliedChange> { new() { TweakId = "optiscaler", Action = new FitzLauncher.Core.Models.FileReplaceAction { NewVersion = "OptiScaler" } } };
        var nr = new List<FitzLauncher.Core.Models.AppliedChange> { new() { TweakId = "optiscaler", Action = new FitzLauncher.Core.Models.FileReplaceAction { NewVersion = "AMDNR" } } };
        Assert.False(FitzLauncher.Core.Upscalers.OptiScalerStatus.IsAmdNr(stock));
        Assert.True(FitzLauncher.Core.Upscalers.OptiScalerStatus.IsAmdNr(nr));
        Assert.True(FitzLauncher.Core.Upscalers.OptiScalerStatus.IsInstalled(stock));
        Assert.False(FitzLauncher.Core.Upscalers.OptiScalerStatus.IsInstalled([]));
    }
}
