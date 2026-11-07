using FitzLauncher.Core.Models;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Core.Tests;

public class OptimizationPlannerTests
{
    private static HardwareProfile Hw(int refresh = 120, double vramGb = 8, bool rtss = false) => new()
    {
        Gpus = [new GpuInfo { Name = "NVIDIA GeForce RTX 4070", Vendor = GpuVendor.Nvidia, VramBytes = (long)(vramGb * 1024 * 1024 * 1024), IsDiscrete = true }],
        Displays = [new DisplayInfo { DeviceName = "DISPLAY1", CurrentRefreshHz = refresh, MaxRefreshHz = refresh, IsPrimary = true }],
        OsBuild = 22631,
        RtssInstalled = rtss,
        RtssPath = rtss ? "/rtss" : null,
    };

    private static (Game, GameScanResult) Unreal(string cfgDir, int shadow = 4)
    {
        var game = new Game { Name = "UE Game", ExePath = "/Games/UE/UE.exe", InstallDir = "/Games/UE" };
        var scan = new GameScanResult
        {
            Engine = GameEngine.Unreal5,
            ConfigDirectories = [cfgDir],
            Upscalers = [Upscaler.Dlss],
            CurrentSettings = new(StringComparer.OrdinalIgnoreCase) { ["GUS:sg.ShadowQuality"] = shadow.ToString(), ["GUS:sg.TextureQuality"] = "4" },
        };
        return (game, scan);
    }

    [Fact]
    public void Unreal_frame_cap_writes_both_ini_files_at_target()
    {
        var (game, scan) = Unreal("/cfg/Saved/Config/Windows");
        var plan = new OptimizationPlanner().Build(game, scan, Hw(), 117);

        var cap = plan.Tweaks.Single(t => t.Id == OptimizationPlanner.FrameCapId);
        Assert.True(cap.PartOfOneClick);
        var ini = cap.Actions.OfType<IniSetAction>().ToList();
        Assert.Contains(ini, a => a.Key == "FrameRateLimit" && a.Value == "117.000000" && a.FilePath.EndsWith("GameUserSettings.ini"));
        Assert.Contains(ini, a => a.Key == "t.MaxFPS" && a.Value == "117" && a.FilePath.EndsWith("Engine.ini"));
    }

    [Fact]
    public void Rtss_is_preferred_for_the_cap_when_installed()
    {
        var (game, scan) = Unreal("/cfg/Saved/Config/Windows");
        var plan = new OptimizationPlanner().Build(game, scan, Hw(rtss: true), 100);
        var cap = plan.Tweaks.Single(t => t.Id == OptimizationPlanner.FrameCapId);
        var rtss = Assert.IsType<RtssFrameLimitAction>(Assert.Single(cap.Actions));
        Assert.Equal(100, rtss.Limit);
        Assert.Equal("UE.exe", rtss.ExeName);
    }

    [Fact]
    public void One_click_never_includes_quality_reductions_or_experimental_tweaks()
    {
        var (game, scan) = Unreal("/cfg/Saved/Config/Windows");
        var plan = new OptimizationPlanner().Build(game, scan, Hw(), 110);
        Assert.NotEmpty(plan.OneClick);
        Assert.All(plan.OneClick, t =>
        {
            Assert.Equal(VisualImpact.None, t.VisualImpact);
            Assert.NotEqual(TweakRisk.Experimental, t.Risk);
            Assert.True(t.IsAutomatic);
        });
        // Graphics changes are offered as suggestions, never applied silently.
        Assert.Contains(plan.Suggestions, t => t.Id == "ue-sg.ShadowQuality" && t.Title.Contains("Cinematic → Epic"));
    }

    [Fact]
    public void Texture_quality_and_resolution_are_never_suggested()
    {
        var (game, scan) = Unreal("/cfg/Saved/Config/Windows");
        scan.CurrentSettings["GUS:sg.ResolutionQuality"] = "100";
        var plan = new OptimizationPlanner().Build(game, scan, Hw(), 117);
        Assert.DoesNotContain(plan.Tweaks, t => t.Id.Contains("Texture") || t.Id.Contains("Resolution"));
    }

    [Fact]
    public void Graphics_suggestions_become_recommended_when_last_session_missed_target()
    {
        var (game, scan) = Unreal("/cfg/Saved/Config/Windows");
        var last = new SessionSummary
        {
            HasFpsData = true, AverageFps = 95, OnePercentLowFps = 80, TimeAtTarget = 0.4,
            Start = DateTimeOffset.Now.AddHours(-1), End = DateTimeOffset.Now,
        };
        var plan = new OptimizationPlanner().Build(game, scan, Hw(), 117, last);
        Assert.True(plan.Tweaks.Single(t => t.Id == "ue-sg.ShadowQuality").Recommended);
        Assert.True(plan.Tweaks.Single(t => t.Id == "dlss-quality").Recommended);
        Assert.Equal(90, plan.SuggestedTarget); // the 80 fps 1% low can't hold 100+, so suggest 90 and the fixes
    }

    [Fact]
    public void Texture_pool_is_sized_from_vram()
    {
        Assert.Equal(3072, OptimizationPlanner.StreamingPoolMb(8));
        Assert.Equal(4096, OptimizationPlanner.StreamingPoolMb(12));
        Assert.Equal(6144, OptimizationPlanner.StreamingPoolMb(16));
        Assert.Equal(0, OptimizationPlanner.StreamingPoolMb(0));
    }

    [Fact]
    public void Unknown_engine_without_rtss_gives_driver_instructions()
    {
        var game = new Game { Name = "X", ExePath = "/X/x.exe" };
        var plan = new OptimizationPlanner().Build(game, new GameScanResult(), Hw(), 90);
        var cap = plan.Tweaks.Single(t => t.Id == OptimizationPlanner.FrameCapId);
        Assert.False(cap.IsAutomatic);
        Assert.Contains("NVIDIA Control Panel", Assert.IsType<ManualStepAction>(cap.Actions[0]).Instructions);
    }

    [Fact]
    public void Source_games_get_fps_max_launch_option()
    {
        var game = new Game { Name = "HL2", ExePath = "/HL2/hl2.exe" };
        var plan = new OptimizationPlanner().Build(game, new GameScanResult { Engine = GameEngine.Source }, Hw(), 117);
        var cap = plan.Tweaks.Single(t => t.Id == OptimizationPlanner.FrameCapId);
        Assert.Equal("+fps_max 117", Assert.IsType<LaunchArgumentAction>(cap.Actions[0]).Arguments);
    }
}

public class TargetAdvisorTests
{
    private static HardwareProfile Display(int hz) => new() { Displays = [new DisplayInfo { MaxRefreshHz = hz, CurrentRefreshHz = hz, IsPrimary = true }] };

    [Fact]
    public void Suggests_117_on_120hz_without_history() =>
        Assert.Equal(117, TargetAdvisor.Suggest(Display(120), null, null).target);

    [Fact]
    public void Suggests_nothing_on_60hz() =>
        Assert.Null(TargetAdvisor.Suggest(Display(60), null, 117).target);

    [Theory]
    [InlineData(130, 106, 110)] // within 5% of 110
    [InlineData(125, 100, 100)]
    [InlineData(140, 120, 117)]
    public void Suggests_highest_target_the_lows_can_hold(double avg, double low, int expected)
    {
        var last = new SessionSummary { HasFpsData = true, AverageFps = avg, OnePercentLowFps = low };
        Assert.Equal(expected, TargetAdvisor.Suggest(Display(144), last, 117).target);
    }

    [Fact]
    public void Never_suggests_above_refresh_rate()
    {
        var last = new SessionSummary { HasFpsData = true, AverageFps = 200, OnePercentLowFps = 180 };
        Assert.Equal(90, TargetAdvisor.Suggest(Display(100), last, 117).target);
    }
}

public class SystemAdvisorTests
{
    [Fact]
    public void Flags_wrong_refresh_rate_jedec_ram_and_single_channel()
    {
        var hw = new HardwareProfile
        {
            Displays = [new DisplayInfo { DeviceName = "DISPLAY1", CurrentRefreshHz = 60, MaxRefreshHz = 144 }],
            RamType = "DDR5", RamConfiguredMhz = 4800, RamSticks = 1, RamBytes = 16L << 30,
            RtssInstalled = true,
        };
        var ids = SystemAdvisor.Analyse(hw).Select(t => t.Id).ToList();
        Assert.Contains("display-refresh-DISPLAY1", ids);
        Assert.Contains("xmp", ids);
        Assert.Contains("dual-channel", ids);
        Assert.DoesNotContain("install-rtss", ids);
    }

    [Fact]
    public void X3d_cpus_keep_balanced_power_plan()
    {
        var tuning = SystemAdvisor.Tune(new HardwareProfile { CpuHasVCache = true, RamBytes = 32L << 30, OsBuild = 22631 });
        Assert.False(tuning.SwitchToHighPerformancePlan);
        Assert.False(tuning.UseTimerResolution); // Windows 11: per-process timer
    }

    [Fact]
    public void Low_ram_machines_get_aggressive_memory_guard()
    {
        var tuning = SystemAdvisor.Tune(new HardwareProfile { RamBytes = 8L << 30 });
        Assert.Equal(2048, tuning.FreeMemoryThresholdMb);
        Assert.True(tuning.TrimBackgroundWorkingSets);
    }
}
