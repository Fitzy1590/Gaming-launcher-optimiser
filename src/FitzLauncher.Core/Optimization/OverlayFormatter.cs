using System.Globalization;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>The numbers the overlay can show at one moment.</summary>
public readonly record struct OverlayValues(
    double Fps, double OnePercentLow, double? Watts, double? CpuTempC, double? GpuTempC, double? GpuLoad, int? Cap,
    bool ThermalThrottling = false, int? BenchmarkSecondsLeft = null, string? Note = null, DateTime? Now = null,
    OtherOsd? Other = null, int? LimitWatts = null);

/// <summary>What another on-screen display (MSI Afterburner's lines in RTSS) is already showing.</summary>
public readonly record struct OtherOsd(bool ShowsFps, bool ShowsPower, int Temperatures);

/// <summary>Reads RTSS OSD text from other apps to see which numbers are already on screen.</summary>
public static partial class OsdTextAnalyzer
{
    [System.Text.RegularExpressions.GeneratedRegex(@"<[^>]*>")] private static partial System.Text.RegularExpressions.Regex Tags();
    [System.Text.RegularExpressions.GeneratedRegex(@"\bFPS\b|framerate", System.Text.RegularExpressions.RegexOptions.IgnoreCase)] private static partial System.Text.RegularExpressions.Regex Fps();
    [System.Text.RegularExpressions.GeneratedRegex(@"\d\s*W\b")] private static partial System.Text.RegularExpressions.Regex Power();
    [System.Text.RegularExpressions.GeneratedRegex(@"°\s*C")] private static partial System.Text.RegularExpressions.Regex Temp();

    public static OtherOsd Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var plain = Tags().Replace(text, " ");
        return new OtherOsd(Fps().IsMatch(plain), Power().IsMatch(plain), Temp().Matches(plain).Count);
    }
}

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
        // One counter, not two: leave out what Afterburner already shows (one temperature is its GPU one, two are CPU and GPU).
        var other = o.AvoidDuplicates ? v.Other ?? default : default;
        if (o.Fps && v.Fps > 0 && !other.ShowsFps) items.Add((v.Fps.ToString("0", ci), "FPS"));
        if (o.OnePercentLow && v.OnePercentLow > 0) items.Add((v.OnePercentLow.ToString("0", ci), "1% low"));
        if (o.Watts && v.Watts is > 0 and { } w && !other.ShowsPower) items.Add((w.ToString("0", ci), "W"));
        if (o.Limit && v.LimitWatts is { } lim) items.Add(("TDP", lim.ToString("0", ci) + " W"));
        if (o.CpuTemp && v.CpuTempC is { } c && other.Temperatures < 2) items.Add(("CPU", c.ToString("0", ci) + "°"));
        if (o.GpuTemp && v.GpuTempC is { } g && other.Temperatures < 1) items.Add(("GPU", g.ToString("0", ci) + "°"));
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

        var pad = new string(' ', Math.Clamp(o.LeftMargin, 0, 12));
        var body = o.Arrangement == OverlayArrangement.Column ? string.Join("\n", parts.Select(x => pad + x)) : pad + string.Join("   ", parts);
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
