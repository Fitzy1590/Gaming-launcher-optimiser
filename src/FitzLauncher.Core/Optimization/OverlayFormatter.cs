using System.Globalization;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>The numbers the overlay can show at one moment.</summary>
public readonly record struct OverlayValues(
    double Fps, double OnePercentLow, double? Watts, double? CpuTempC, double? GpuTempC, double? GpuLoad, int? Cap,
    bool ThermalThrottling = false, int? BenchmarkSecondsLeft = null, string? Note = null, DateTime? Now = null);

/// <summary>
/// Builds the launcher's RTSS overlay text from <see cref="OverlaySettings"/>: a single tidy row
/// ("117 FPS  45 W  CPU 77°  GPU 75°") or a column, optionally with RTSS colour/size tags.
/// Temporary messages (cooling, benchmark, notes) are added only while they matter.
/// </summary>
public static class OverlayFormatter
{
    private const string Value = "FFFFFF", Label = "9AA4B2", Warn = "FFB020";

    public static string Format(OverlaySettings o, OverlayValues v)
    {
        var items = new List<(string value, string label)>();
        var ci = CultureInfo.InvariantCulture;
        if (o.Fps && v.Fps > 0) items.Add((v.Fps.ToString("0", ci), "FPS"));
        if (o.OnePercentLow && v.OnePercentLow > 0) items.Add((v.OnePercentLow.ToString("0", ci), "1% low"));
        if (o.Watts && v.Watts is > 0 and { } w) items.Add((w.ToString("0", ci), "W"));
        if (o.CpuTemp && v.CpuTempC is { } c) items.Add(("CPU", c.ToString("0", ci) + "°"));
        if (o.GpuTemp && v.GpuTempC is { } g) items.Add(("GPU", g.ToString("0", ci) + "°"));
        if (o.GpuLoad && v.GpuLoad is { } l) items.Add(("GPU", l.ToString("0", ci) + "%"));
        if (o.Cap && v.Cap is { } cap and > 0) items.Add(("cap", cap.ToString(ci)));
        if (o.Clock && v.Now is { } now) items.Add((now.ToString("HH:mm", ci), ""));

        var extras = new List<string>();
        if (v.ThermalThrottling && v.Cap is { } tc) extras.Add($"cooling: cap {tc}");
        if (v.Note is { Length: > 0 } note) extras.Add(note);
        if (v.BenchmarkSecondsLeft is { } b) extras.Add($"benchmark {b}s");

        var parts = items.Select(i => Piece(o, i.value, i.label)).ToList();
        parts.AddRange(extras.Select(e => o.Coloured ? $"<C={Warn}>{e}<C>" : e));
        if (parts.Count == 0) return "";

        var body = o.Arrangement == OverlayArrangement.Column ? string.Join("\n", parts) : string.Join("   ", parts);
        var size = Math.Clamp(o.SizePercent, 50, 150);
        return o.Coloured && size != 100 ? $"<S={size}>{body}<S>" : body;
    }

    /// <summary>
    /// "117 FPS", "45 W", "CPU 77°": labels for FPS/W follow the number, names (CPU/GPU/cap) go before it.
    /// </summary>
    private static string Piece(OverlaySettings o, string a, string b)
    {
        // Items stored as (number, unit) or (name, value); a leading letter means (name, value).
        var nameFirst = a.Length > 0 && char.IsLetter(a[0]);
        if (b.Length == 0) return o.Coloured ? $"<C={Value}>{a}<C>" : a;
        if (!o.Coloured) return $"{a} {b}";
        return nameFirst ? $"<C={Label}>{a}<C> <C={Value}>{b}<C>" : $"<C={Value}>{a}<C> <C={Label}>{b}<C>";
    }
}
