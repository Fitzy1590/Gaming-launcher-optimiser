using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace FitzLauncher.App.Services;

/// <summary>
/// Decodes cover art off the UI thread at display size (not full resolution) and freezes it, so
/// scrolling a big library stays smooth and memory stays low (~150 KB per card instead of ~2 MB).
/// </summary>
public static class ImageLoader
{
    private static readonly ConcurrentDictionary<(string, int), BitmapSource> Cache = new();

    public static Task<BitmapSource?> LoadAsync(string? path, int decodeWidth) => Task.Run(() => Load(path, decodeWidth));

    public static BitmapSource? Load(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (Cache.TryGetValue((path!, decodeWidth), out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            using (var fs = File.OpenRead(path))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            Cache[(path!, decodeWidth)] = bmp;
            return bmp;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public static void Invalidate(string? path)
    {
        if (path is null) return;
        foreach (var key in Cache.Keys.Where(k => k.Item1 == path).ToList()) Cache.TryRemove(key, out _);
    }
}
