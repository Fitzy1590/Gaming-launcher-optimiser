using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Mods;

namespace FitzLauncher.Core.Emulation;

public sealed record EmuAsset(string Name, string Url, long Size, string? Sha256);

public sealed record EmuRelease(string Tag, string Name, DateTimeOffset PublishedAt, bool Prerelease, List<EmuAsset> Assets);

/// <summary>Reads GitHub's releases JSON (SPEC.md §1.2). Release tags differ a lot between emulators (v2.9.167, build-&lt;hash&gt;, 2127.0-rc2, a short commit), so the tag is only an identity and the publish date decides what is newer.</summary>
public static class GitHubReleaseParser
{
    public static EmuRelease? ParseOne(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
        if (e.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        var assets = new List<EmuAsset>();
        if (e.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null) continue;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                string? sha = null;
                if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String && dg.GetString() is { } ds && ds.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = ds[7..].ToLowerInvariant();
                assets.Add(new EmuAsset(name, url, size, sha));
            }
        var published = e.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pd) ? pd : DateTimeOffset.MinValue;
        var name2 = e.TryGetProperty("name", out var rn) && rn.GetString() is { Length: > 0 } rns ? rns : tag.GetString()!;
        return new EmuRelease(tag.GetString()!, name2, published, e.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True, assets);
    }

    public static EmuRelease? ParseLatest(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return ParseOne(doc.RootElement); }
        catch (JsonException) { return null; }
    }

    /// <summary>Newest first; nightlies (prereleases) only when asked for.</summary>
    public static List<EmuRelease> ParseList(string json, bool includePrerelease)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return [.. doc.RootElement.EnumerateArray().Select(ParseOne).OfType<EmuRelease>().Where(r => includePrerelease || !r.Prerelease).OrderByDescending(r => r.PublishedAt)];
        }
        catch (JsonException) { return []; }
    }
}

public static partial class AssetPicker
{
    private static readonly string[] Unwanted =
        ["linux", "mac", "osx", "darwin", "appimage", "flatpak", "android", "arm64", "aarch64", "armv", "debug", "symbols", "pdb", "source", "src", "setup", "installer", "msi", "sha256", "sig", "txt", "deb", "rpm", "ios", "wasm"];

    [GeneratedRegex(@"\.(zip|7z)$", RegexOptions.IgnoreCase)]
    private static partial Regex Archive();

    /// <summary>
    /// Picks the Windows portable download. Only .zip and .7z qualify; names mentioning other systems, installers, symbols or sources are out.
    /// The source's own pattern, then "win", "x64" and "msvc" in the name, raise the score; .zip is preferred as it needs no extra unpacker.
    /// </summary>
    public static EmuAsset? Pick(IEnumerable<EmuAsset> assets, string? pattern)
    {
        Regex? custom = null;
        if (!string.IsNullOrWhiteSpace(pattern)) { try { custom = new Regex(pattern, RegexOptions.IgnoreCase); } catch (ArgumentException) { } }
        EmuAsset? best = null;
        var bestScore = 0;
        foreach (var a in assets)
        {
            if (!Archive().IsMatch(a.Name)) continue;
            var lower = a.Name.ToLowerInvariant();
            if (Unwanted.Any(w => lower.Contains(w))) continue;
            var score = 1;
            if (custom?.IsMatch(a.Name) == true) score += 10;
            if (lower.Contains("win")) score += 5;
            if (lower.Contains("x64") || lower.Contains("amd64") || lower.Contains("x86_64") || lower.Contains("64")) score += 3;
            if (lower.Contains("msvc")) score += 2;
            if (lower.EndsWith(".zip")) score += 1;
            if (score > bestScore) { best = a; bestScore = score; }
        }
        return best;
    }
}

public static class EmuReleaseCompare
{
    /// <summary>Newer when the tag differs and the release was published later (an unknown install date counts as older).</summary>
    public static bool IsNewer(string? installedTag, DateTimeOffset? installedPublished, EmuRelease latest)
    {
        if (installedTag is null) return true;
        if (installedTag.Equals(latest.Tag, StringComparison.OrdinalIgnoreCase)) return false;
        return installedPublished is not { } ip || latest.PublishedAt > ip;
    }

    private static readonly string[] AllowedHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"];

    /// <summary>Emulator downloads come only from GitHub's own hosts, over https.</summary>
    public static bool IsAllowedHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>A short folder name for a release tag (tags can hold characters a folder can't).</summary>
    public static string FolderName(string tag)
    {
        var clean = Regex.Replace(tag, @"[^\w.\-]", "_");
        return clean.Length > 60 ? clean[..60] : clean;
    }
}

/// <summary>Unpacks an emulator download with the same traversal checks as mods, then drops a single wrapping folder.</summary>
public static class EmuArchive
{
    public static void ExtractAll(string archive, string destDir)
    {
        var entries = ModArchive.ListEntries(archive);
        var plan = entries.Select(e => new PlannedFile(e, e)).ToList();
        if (plan.Any(p => !ModInstallPlanner.IsSafeRelative(p.Dest))) throw new InvalidDataException("The download holds an unsafe path, so it was not unpacked.");
        Directory.CreateDirectory(destDir);
        ModArchive.Extract(archive, plan, destDir);
        FlattenSingleRoot(destDir);
    }

    /// <summary>"emu-1.2/…" with nothing beside it becomes the contents of the folder itself.</summary>
    public static void FlattenSingleRoot(string dir)
    {
        var files = Directory.GetFiles(dir);
        var subs = Directory.GetDirectories(dir);
        if (files.Length > 0 || subs.Length != 1) return;
        var inner = subs[0];
        var tmp = dir.TrimEnd(Path.DirectorySeparatorChar) + ".flatten";
        Directory.Move(inner, tmp);
        Directory.Delete(dir);
        Directory.Move(tmp, dir);
    }

    /// <summary>The emulator's program inside an unpacked folder (shallowest match).</summary>
    public static string? FindExe(string dir, IEnumerable<string> exeNames)
    {
        var names = exeNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
            .Where(f => names.Contains(Path.GetFileName(f)))
            .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)).FirstOrDefault();
    }
}

/// <summary>What Fitz installed for one emulator (the shared data folder stays outside the version folders).</summary>
public sealed class InstalledEmulator
{
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public string Exe { get; set; } = "";
    public List<string> Previous { get; set; } = [];
}
