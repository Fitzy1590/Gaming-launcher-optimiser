namespace FitzLauncher.Core.Upscalers;

public enum UpscaleMode { Quality, Balanced, Performance }

/// <summary>
/// OptiScaler.ini key/values for a GPU and quality mode (SPEC §13.2). Section and key names are from
/// OptiScaler's own OptiScaler.ini ([Upscalers] Dx12Upscaler, [FSR] UpscalerIndex, [QualityOverrides]).
/// Anything not listed is left as the template has it.
/// </summary>
public static class OptiScalerSettings
{
    /// <summary>Render-scale divisors OptiScaler's template documents per mode (1.5x = 67 %).</summary>
    public static double Ratio(UpscaleMode m) => m switch
    {
        UpscaleMode.Quality => 1.5,
        UpscaleMode.Balanced => 1.7,
        _ => 2.0,
    };

    public static string Label(UpscaleMode m) => m switch
    {
        UpscaleMode.Quality => "Quality (1.5x)",
        UpscaleMode.Balanced => "Balanced (1.7x)",
        _ => "Performance (2.0x)",
    };

    public static IReadOnlyList<(string Section, string Key, string Value)> For(GpuFamily family, UpscaleMode? mode)
    {
        var s = new List<(string, string, string)>();
        // RTX cards keep OptiScaler's own choice (DLSS). Everyone else gets FSR via "ffx".
        if (family != GpuFamily.NvidiaRtx) s.Add(("Upscalers", "Dx12Upscaler", "ffx"));
        // RDNA4 keeps the default (FSR 4); other GPUs run FSR 3.1.5 (index 1), which works on any hardware.
        if (family != GpuFamily.AmdRdna4 && family != GpuFamily.NvidiaRtx) s.Add(("FSR", "UpscalerIndex", "1"));
        if (mode is { } m)
        {
            s.Add(("QualityOverrides", "QualityRatioOverrideEnabled", "true"));
            var key = m switch { UpscaleMode.Quality => "QualityRatioQuality", UpscaleMode.Balanced => "QualityRatioBalanced", _ => "QualityRatioPerformance" };
            s.Add(("QualityOverrides", key, Ratio(m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
        }
        return s;
    }
}
