using FitzLauncher.Core.Models;
using FitzLauncher.Core.Upscalers;

namespace FitzLauncher.Core.Tests;

public class OptiScalerReplacedTests
{
    private static AppliedChange Change(string target, string source) => new()
    {
        TweakId = "optiscaler",
        Action = new FileReplaceAction { TargetPath = target, SourcePath = source },
    };

    [Fact]
    public void FlagsAFileWhoseSizeChanged_AndIgnoresTheIniAndUnreadableFiles()
    {
        var ledger = new[] { Change("g/dxgi.dll", "s/dxgi.dll"), Change("g/OptiScaler.ini", "s/OptiScaler.ini"), Change("g/x.dll", "s/x.dll") };
        long? Len(string p) => p switch { "g/dxgi.dll" => 10, "s/dxgi.dll" => 99, "g/OptiScaler.ini" => 1, "s/OptiScaler.ini" => 5, _ => null };
        Assert.Equal(["dxgi.dll"], OptiScalerStatus.ReplacedFiles(ledger, Len));
    }

    [Fact]
    public void SameSizeIsFine() =>
        Assert.Empty(OptiScalerStatus.ReplacedFiles([Change("a.dll", "b.dll")], _ => 7));
}
