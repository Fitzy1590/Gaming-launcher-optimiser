namespace FitzLauncher.Core.Models;

/// <summary>
/// AMD Software features to switch on/off while one game runs (null = leave as it is). They're global driver
/// settings, so the launcher applies them at launch and puts the previous values back afterwards.
/// </summary>
public sealed class AmdProfile
{
    public bool? AntiLag { get; set; }
    public bool? Chill { get; set; }
    public int ChillMinFps { get; set; } = 60;
    public int ChillMaxFps { get; set; } = 110;
    public bool? SuperResolution { get; set; }
    public bool? FluidMotionFrames { get; set; }
    public bool? ImageSharpening { get; set; }
    public int SharpeningPercent { get; set; } = 80;
    public bool? Boost { get; set; }

    public bool Any => AntiLag is not null || Chill is not null || SuperResolution is not null || FluidMotionFrames is not null || ImageSharpening is not null || Boost is not null;
}

/// <summary>What the driver had before a session (for restoring), and what it supports.</summary>
public sealed class AmdFeatureState
{
    public bool? AntiLag { get; set; }
    public bool? Chill { get; set; }
    public int? ChillMinFps { get; set; }
    public int? ChillMaxFps { get; set; }
    public bool? SuperResolution { get; set; }
    public bool? FluidMotionFrames { get; set; }
    public bool? ImageSharpening { get; set; }
    public int? SharpeningPercent { get; set; }
    public bool? Boost { get; set; }
}
