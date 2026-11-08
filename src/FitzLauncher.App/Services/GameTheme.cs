using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using FitzLauncher.Core.Artwork;

namespace FitzLauncher.App.Services;

/// <summary>
/// Retints the UI to the selected game: its signature colour becomes the accent (Play, focus ring, tabs, FPS
/// target) and a deep shade of it tints the background, with a short fade between games. Brushes are swapped
/// at the application level, so every view using DynamicResource follows.
/// </summary>
public static class GameTheme
{
    public static bool Enabled { get; set; } = true;
    private static ArtColours _current = ArtPalette.Default;

    public static ArtColours FromImage(ImageSource? image)
    {
        if (image is not BitmapSource src || src.PixelWidth == 0) return ArtPalette.Default;
        try
        {
            var scale = Math.Min(1, 64d / Math.Max(src.PixelWidth, src.PixelHeight));
            BitmapSource small = new TransformedBitmap(src, new ScaleTransform(scale, scale));
            if (small.Format != PixelFormats.Bgra32) small = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            var w = small.PixelWidth;
            var h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);
            return ArtPalette.From(px, w, h);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException) { return ArtPalette.Default; }
    }

    public static void Apply(ImageSource? art) => Apply(Enabled ? FromImage(art) : ArtPalette.Default);

    public static void Apply(ArtColours c)
    {
        if (c == _current || Application.Current is not { } app) return;
        var prev = _current;
        _current = c;
        var r = app.Resources;
        r["Accent"] = Fade(prev.Accent, c.Accent);
        r["AccentSoft"] = new SolidColorBrush(Col(c.Accent, 0x26));
        r["Deep"] = Fade(prev.Deep, c.Deep);
        r["Bg"] = Fade(prev.Bg, c.Bg);
        r["PaneBg"] = new SolidColorBrush(Col(c.Bg, 0xE6));
        r["ArtScrim"] = Gradient(new Point(0, 0), new Point(1, 0), (Col(c.Deep, 0xF0), 0), (Col(c.Deep, 0xA6), 0.55), (Col(c.Deep, 0x59), 1));
        r["ArtFade"] = Gradient(new Point(0, 0), new Point(0, 1), (Col(c.Deep, 0x00), 0.45), (Col(c.Deep, 0xFF), 1));
    }

    private static Color Col(Rgb c, byte a = 0xFF) => Color.FromArgb(a, c.R, c.G, c.B);

    private static SolidColorBrush Fade(Rgb from, Rgb to)
    {
        var b = new SolidColorBrush(Col(from));
        if (SystemParameters.ClientAreaAnimation)
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Col(to), TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else b.Color = Col(to);
        return b;
    }

    private static LinearGradientBrush Gradient(Point start, Point end, params (Color c, double o)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (c, o) in stops) g.GradientStops.Add(new GradientStop(c, o));
        g.Freeze();
        return g;
    }
}
