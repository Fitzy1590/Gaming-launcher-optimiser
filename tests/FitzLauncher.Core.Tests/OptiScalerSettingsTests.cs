using FitzLauncher.Core.Models;
using FitzLauncher.Core.Upscalers;

namespace FitzLauncher.Core.Tests;

public class OptiScalerSettingsTests
{
    [Fact]
    public void Rdna4_UsesFfxAndKeepsFsr4Default()
    {
        var s = OptiScalerSettings.For(GpuFamily.AmdRdna4, null);
        Assert.Contains(("Upscalers", "Dx12Upscaler", "ffx"), s);
        Assert.DoesNotContain(s, x => x.Key == "UpscalerIndex");
    }

    [Fact]
    public void OlderAmd_UsesFsr31()
    {
        var s = OptiScalerSettings.For(GpuFamily.AmdRdna3, null);
        Assert.Contains(("FSR", "UpscalerIndex", "1"), s);
    }

    [Fact]
    public void NvidiaRtx_LeavesTemplateAlone()
    {
        Assert.Empty(OptiScalerSettings.For(GpuFamily.NvidiaRtx, null));
    }

    [Theory]
    [InlineData(UpscaleMode.Quality, "QualityRatioQuality", "1.5")]
    [InlineData(UpscaleMode.Balanced, "QualityRatioBalanced", "1.7")]
    [InlineData(UpscaleMode.Performance, "QualityRatioPerformance", "2.0")]
    public void Mode_SetsOnlyItsRatioAndEnablesOverride(UpscaleMode mode, string key, string ratio)
    {
        var s = OptiScalerSettings.For(GpuFamily.AmdRdna3, mode);
        Assert.Contains(("QualityOverrides", "QualityRatioOverrideEnabled", "true"), s);
        Assert.Contains(("QualityOverrides", key, ratio), s);
        Assert.Single(s, x => x.Key.StartsWith("QualityRatio") && x.Key != "QualityRatioOverrideEnabled");
    }

    [Fact]
    public void Tweak_AddsIniEditsAfterFileCopies_OnlyWhenTemplateShips()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var opti = Path.Combine(root, "opti"); var game = Path.Combine(root, "game");
            Directory.CreateDirectory(opti); Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(opti, "OptiScaler.dll"), "x");
            var without = UpscalerAdvisor.OptiScalerTweak(opti, game, _ => false, GpuFamily.AmdRdna3, UpscaleMode.Quality)!;
            Assert.DoesNotContain(without.Actions, a => a is IniSetAction);

            File.WriteAllText(Path.Combine(opti, "OptiScaler.ini"), "[Upscalers]\nDx12Upscaler=auto\n");
            var with = UpscalerAdvisor.OptiScalerTweak(opti, game, _ => false, GpuFamily.AmdRdna3, UpscaleMode.Quality)!;
            var lastCopy = with.Actions.FindLastIndex(a => a is FileReplaceAction);
            var firstIni = with.Actions.FindIndex(a => a is IniSetAction);
            Assert.True(firstIni > lastCopy);
        }
        finally { Directory.Delete(root, true); }
    }
}
