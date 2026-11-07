using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Runtimes;

public enum RedistKind { VcRedist, DirectX, DotNetFramework, PhysX, OpenAL, Xna, Other }

/// <summary>A runtime installer found in a repack, classified so it can be checked and installed silently.</summary>
public sealed record RedistInfo(string Path, RedistKind Kind, string DisplayName, string? Year, string? Arch, string? Version, string? SilentArgs)
{
    /// <summary>Unique identity, so the same runtime bundled twice is only installed once.</summary>
    public string Key => $"{Kind}|{Year}|{Arch}";
}

/// <summary>Is this runtime already on the PC? Implemented with registry/System32 checks on Windows.</summary>
public interface IRedistProbe
{
    bool IsInstalled(RedistInfo redist);
}

public static partial class RedistCatalog
{
    [GeneratedRegex(@"(?i)^(vc_?redist|vcredist|dxsetup|dxwebsetup|oalinst|physx|xnafx|dotnetfx|ndp\d+|netfx|dotNetFx)")]
    private static partial Regex RedistName();

    [GeneratedRegex(@"(?i)(20(05|08|10|12|13|15|17|19|22))")]
    private static partial Regex YearRx();

    public static bool LooksLikeRedist(string fileName) => RedistName().IsMatch(fileName);

    /// <param name="productName">FileVersionInfo.ProductName (most reliable), if available.</param>
    public static RedistInfo? Classify(string path, string? productName, string? fileVersion)
    {
        var file = path.Split('\\', '/')[^1];
        var hay = $"{productName} {file} {path}";
        var arch = hay.Contains("x64", StringComparison.OrdinalIgnoreCase) || hay.Contains("amd64", StringComparison.OrdinalIgnoreCase) ? "x64"
                 : hay.Contains("x86", StringComparison.OrdinalIgnoreCase) ? "x86" : null;

        if (Regex.IsMatch(hay, @"(?i)visual c\+\+|vc_?redist|vcredist"))
        {
            // Year from the product name first ("…2015-2022…" → 2015 family), then the folder name.
            var year = YearRx().Match(productName ?? "") is { Success: true } m ? m.Value
                     : YearRx().Match(path) is { Success: true } p ? p.Value : null;
            if (year is "2017" or "2019" or "2022") year = "2015"; // one binary-compatible family (v14)
            var silent = year switch
            {
                "2005" or "2008" => "/q",
                "2010" => "/q /norestart",
                _ => "/install /quiet /norestart",
            };
            return new RedistInfo(path, RedistKind.VcRedist, $"Visual C++ {(year == "2015" ? "2015-2022" : year ?? "")} {arch}".Trim(), year, arch ?? "x86", fileVersion, silent);
        }
        if (file.Equals("DXSETUP.exe", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(hay, @"(?i)directx"))
            return file.Equals("DXSETUP.exe", StringComparison.OrdinalIgnoreCase)
                ? new RedistInfo(path, RedistKind.DirectX, "DirectX (June 2010) runtime", null, null, fileVersion, "/silent")
                : null; // the other files in the DirectX folder are cabinets/helpers
        if (Regex.IsMatch(hay, @"(?i)\.net framework|dotnetfx|ndp\d|netfx"))
            return new RedistInfo(path, RedistKind.DotNetFramework, productName ?? ".NET Framework", YearRx().Match(hay).Value, null, fileVersion, "/q /norestart");
        if (Regex.IsMatch(hay, @"(?i)physx"))
            return new RedistInfo(path, RedistKind.PhysX, "NVIDIA PhysX (legacy)", null, null, fileVersion,
                path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "/qn" : null);
        if (Regex.IsMatch(hay, @"(?i)openal|oalinst"))
            return new RedistInfo(path, RedistKind.OpenAL, "OpenAL", null, null, fileVersion, "/s");
        if (Regex.IsMatch(hay, @"(?i)xna"))
            return new RedistInfo(path, RedistKind.Xna, "XNA Framework 4.0", null, null, fileVersion,
                path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? "/qn" : null);
        return null;
    }

    /// <summary>De-duplicates (repacks often bundle the same runtime several times) and keeps the newest of each.</summary>
    public static List<RedistInfo> Dedupe(IEnumerable<RedistInfo> items) =>
        items.GroupBy(r => r.Key)
             .Select(g => g.OrderByDescending(r => System.Version.TryParse(r.Version, out var v) ? v : new System.Version()).First())
             .OrderBy(r => r.Kind).ThenBy(r => r.Year).ToList();
}
