namespace FitzLauncher.Core.Models;

public enum OverlayArrangement { Row, Column }

/// <summary>What the launcher shows in the in-game overlay (RTSS) and how it's laid out. Global default, or per game.</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Row: one line across the top. Column: one value per line.</summary>
    public OverlayArrangement Arrangement { get; set; } = OverlayArrangement.Row;
    public bool Fps { get; set; } = true;
    public bool OnePercentLow { get; set; }
    public bool Watts { get; set; } = true;
    public bool CpuTemp { get; set; } = true;
    public bool GpuTemp { get; set; } = true;
    public bool GpuLoad { get; set; }
    public bool Cap { get; set; }
    public bool Clock { get; set; }
    /// <summary>Accent-coloured values with dim labels (RTSS colour tags); off for plain text.</summary>
    public bool Coloured { get; set; } = true;
    /// <summary>Text size relative to RTSS's own setting (50-150%).</summary>
    public int SizePercent { get; set; } = 100;

    public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();

    /// <summary>Settings files from before layouts existed: map the old Off / FPS+temps / Detailed choice.</summary>
    public static OverlaySettings FromLegacy(bool on, OverlayStyle style) => new()
    {
        Enabled = on && style != OverlayStyle.Off,
        Cap = style == OverlayStyle.Detailed,
        OnePercentLow = style == OverlayStyle.Detailed,
    };
}
