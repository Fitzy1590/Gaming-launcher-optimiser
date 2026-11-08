namespace FitzLauncher.Core.Artwork;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>WCAG relative luminance.</summary>
    public double Luminance
    {
        get
        {
            static double C(byte v) { var c = v / 255d; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            return 0.2126 * C(R) + 0.7152 * C(G) + 0.0722 * C(B);
        }
    }

    public static double Contrast(Rgb a, Rgb b)
    {
        var (l1, l2) = (Math.Max(a.Luminance, b.Luminance), Math.Min(a.Luminance, b.Luminance));
        return (l1 + 0.05) / (l2 + 0.05);
    }
}

/// <summary>Colours for the UI taken from the selected game's art.</summary>
public sealed record ArtColours(Rgb Accent, Rgb Deep, Rgb Bg, bool FromArt);

/// <summary>
/// Picks the game's signature colour from its cover: the hue with the most saturated, mid-bright pixels.
/// The accent is lifted until dark text on it passes 4.5:1 contrast (Play button), and a very dark
/// version of the same hue tints the background, so the whole screen leans towards the game.
/// </summary>
public static class ArtPalette
{
    public static readonly Rgb DefaultAccent = new(0x3F, 0xD0, 0xFF);
    public static readonly ArtColours Default = new(DefaultAccent, new Rgb(0x07, 0x0B, 0x16), new Rgb(0x0B, 0x10, 0x20), false);
    private static readonly Rgb OnAccent = new(0x04, 0x12, 0x1C);

    /// <param name="bgra">Pixels in BGRA order (4 bytes each), e.g. from a small thumbnail.</param>
    public static ArtColours From(ReadOnlySpan<byte> bgra, int width, int height)
    {
        const int Bins = 36;
        var weight = new double[Bins];
        var sumH = new double[Bins];
        var sumS = new double[Bins];
        var count = width * height;
        var step = Math.Max(1, count / 4000); // sample at most ~4000 pixels
        for (var i = 0; i < count; i += step)
        {
            var o = i * 4;
            if (o + 3 >= bgra.Length) break;
            var (h, s, l) = ToHsl(bgra[o + 2], bgra[o + 1], bgra[o]);
            if (s < 0.25 || l < 0.12 || l > 0.9) continue; // greys, near-black, near-white say nothing about the game
            var w = s * (1 - Math.Abs(l - 0.5) * 1.6);
            var bin = (int)(h / 360 * Bins) % Bins;
            weight[bin] += w;
            sumH[bin] += h * w;
            sumS[bin] += s * w;
        }
        // Smooth across neighbouring bins so a hue split over a bin edge still wins.
        var best = -1;
        var bestW = 0d;
        for (var b = 0; b < Bins; b++)
        {
            var w = weight[b] + 0.5 * (weight[(b + 1) % Bins] + weight[(b + Bins - 1) % Bins]);
            if (w > bestW) { bestW = w; best = b; }
        }
        if (best < 0 || weight[best] <= 0 || bestW < count / (double)step * 0.02) return Default;

        var hue = sumH[best] / weight[best];
        var sat = Math.Clamp(sumS[best] / weight[best], 0.55, 0.9);
        var light = 0.6;
        var accent = FromHsl(hue, sat, light);
        while (Rgb.Contrast(accent, OnAccent) < 4.6 && light < 0.9) accent = FromHsl(hue, sat, light += 0.02);
        var deep = FromHsl(hue, Math.Min(sat, 0.55) * 0.7, 0.05);
        var bg = FromHsl(hue, Math.Min(sat, 0.55) * 0.6, 0.08);
        return new ArtColours(accent, deep, bg, true);
    }

    public static (double h, double s, double l) ToHsl(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255d, g = g8 / 255d, b = b8 / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-6) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1d / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2d / 3 ? p + (q - p) * (2d / 3 - t) * 6 : p;
        }
        h /= 360;
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return new Rgb(B(Hue(p, q, h + 1d / 3)), B(Hue(p, q, h)), B(Hue(p, q, h - 1d / 3)));
    }
}
