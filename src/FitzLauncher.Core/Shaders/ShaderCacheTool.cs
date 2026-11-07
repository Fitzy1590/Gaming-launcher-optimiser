namespace FitzLauncher.Core.Shaders;

public sealed record ShaderCache(string Name, string Path, string Vendor)
{
    public long Bytes { get; init; }
    public int Files { get; init; }
}

/// <summary>
/// Driver and DirectX shader caches. After a GPU driver update, old caches are dead weight and can
/// cause recompiles. Clearing them once makes games rebuild clean caches; the first few minutes of
/// each game will stutter while that happens, then it's smoother than before.
/// Files that are in use (a game is running) are skipped, not forced.
/// </summary>
public static class ShaderCacheTool
{
    public static List<ShaderCache> Locations(string localAppData, string localLow)
    {
        var list = new List<ShaderCache>
        {
            new("DirectX shader cache", Path.Combine(localAppData, "D3DSCache"), "Windows"),
            new("NVIDIA DirectX cache", Path.Combine(localAppData, "NVIDIA", "DXCache"), "NVIDIA"),
            new("NVIDIA OpenGL/Vulkan cache", Path.Combine(localAppData, "NVIDIA", "GLCache"), "NVIDIA"),
            new("NVIDIA per-driver cache", Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "DXCache"), "NVIDIA"),
            new("NVIDIA per-driver GL cache", Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "GLCache"), "NVIDIA"),
            new("AMD DirectX cache", Path.Combine(localAppData, "AMD", "DxCache"), "AMD"),
            new("AMD DirectX 12 cache", Path.Combine(localAppData, "AMD", "DxcCache"), "AMD"),
            new("AMD Vulkan cache", Path.Combine(localAppData, "AMD", "VkCache"), "AMD"),
            new("AMD OpenGL cache", Path.Combine(localAppData, "AMD", "GLCache"), "AMD"),
            new("Intel shader cache", Path.Combine(localLow, "Intel", "ShaderCache"), "Intel"),
        };
        return list.Where(c => Directory.Exists(c.Path)).Select(Measure).ToList();
    }

    public static ShaderCache Measure(ShaderCache c)
    {
        long bytes = 0;
        var files = 0;
        foreach (var f in SafeFiles(c.Path))
        {
            try { bytes += new FileInfo(f).Length; files++; } catch (IOException) { }
        }
        return c with { Bytes = bytes, Files = files };
    }

    /// <summary>Deletes cache files. Returns bytes freed and files skipped because they're in use.</summary>
    public static (long freed, int skipped) Clear(ShaderCache c)
    {
        long freed = 0;
        var skipped = 0;
        foreach (var f in SafeFiles(c.Path))
        {
            try
            {
                var len = new FileInfo(f).Length;
                File.Delete(f);
                freed += len;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return (freed, skipped);
    }

    private static IEnumerable<string> SafeFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            : [];
}
