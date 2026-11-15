using System.Text.RegularExpressions;
using FitzLauncher.Core.Emulation;

namespace FitzLauncher.Core.Upscalers;

/// <summary>
/// Picks the AMD NR download from the releases list of 3zwr1/AMD-NR---OptiScaler (FEATURE_PROGRESS §7b N0).
/// Every release there is a pre-release, so GitHub's "latest" endpoint finds nothing: list them and choose.
/// </summary>
public static partial class AmdNrRelease
{
    public const string Repo = "3zwr1/AMD-NR---OptiScaler";
    public const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=15";
    private const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

    /// <summary>Files that must be in the unpacked zip; they come from one release and are applied together.</summary>
    public static readonly string[] RequiredFiles = ["OptiScaler.dll", "LmxxfNrRuntime.dll", "LmxxfNrRuntime.pak"];

    // "AMDNR-v0.3.5.8R23.zip" only: not the launcher exe, the Mochi component zip or a danielblnc runtime zip.
    [GeneratedRegex(@"^AMDNR-v\d[\w.\-]*\.zip$", RegexOptions.IgnoreCase)] private static partial Regex MainZip();

    /// <summary>
    /// The newest release with the main zip attached, its size known and a SHA-256 published by GitHub, hosted under this
    /// repository's own releases. A release without a digest is skipped: we never install what we cannot check.
    /// </summary>
    public static (EmuRelease Release, EmuAsset Asset)? Pick(IEnumerable<EmuRelease> releases)
    {
        foreach (var r in releases.OrderByDescending(r => r.PublishedAt))
        {
            var a = r.Assets.FirstOrDefault(a => MainZip().IsMatch(a.Name) && a.Size > 0 && a.Sha256 is { Length: 64 }
                && a.Url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase) && EmuReleaseCompare.IsAllowedHost(a.Url));
            if (a is not null) return (r, a);
        }
        return null;
    }

    /// <summary>Room for the zip plus its unpacked copy and some slack (the zip is about 526 MB).</summary>
    public static long RequiredFreeBytes(EmuAsset asset) => asset.Size * 2 + 200L * 1024 * 1024;

    /// <summary>The unpacked folder holds every file that has to travel together.</summary>
    public static IReadOnlyList<string> MissingFiles(Func<string, bool> exists) => RequiredFiles.Where(f => !exists(f)).ToList();
}
