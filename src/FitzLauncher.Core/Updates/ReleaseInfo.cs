using System.Text.Json;

namespace FitzLauncher.Core.Updates;

/// <summary>The latest GitHub release, as far as the updater needs it.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string? AssetApiUrl, string? AssetDownloadUrl, string HtmlUrl, DateTimeOffset? Published)
{
    public const string AssetName = "FitzLauncher-win-x64.zip";
    public const string Repository = "Fitzy1590/Gaming-launcher-optimiser";
    public const string LatestUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>Parses GitHub's "latest release" JSON. Returns null if it has no usable version tag.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!TryParseVersion(tag, out var version)) return null;

        string? apiUrl = null, downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != AssetName) continue;
                apiUrl = a.GetProperty("url").GetString();
                downloadUrl = a.GetProperty("browser_download_url").GetString();
            }
        }
        DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : null;
        return new ReleaseInfo(version, tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            apiUrl, downloadUrl,
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            published);
    }

    /// <summary>"v0.3.12" / "0.3.12" → 0.3.12 (missing parts count as 0).</summary>
    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash > 0) s = s[..dash];
        if (!Version.TryParse(s, out var v)) return false;
        version = Normalise(v);
        return true;
    }

    public static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public bool IsNewerThan(Version current) => Version > Normalise(current);
}
