using System.Text.RegularExpressions;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.GameUpdates;

/// <summary>Version parsing and the "is there a newer official version" decision.</summary>
public static partial class GameUpdateLogic
{
    /// <summary>Compares dotted versions numerically ("1.10" > "1.9"); null when either has no number.</summary>
    public static int? Compare(string? a, string? b)
    {
        var pa = Parts(a);
        var pb = Parts(b);
        if (pa.Count == 0 || pb.Count == 0) return null;
        for (var i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            var x = i < pa.Count ? pa[i] : 0;
            var y = i < pb.Count ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static List<long> Parts(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return [];
        var m = DottedNumber().Match(v);
        return m.Success ? m.Value.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToList() : [];
    }

    /// <summary>The version in a patch-notes headline ("Patch 1.6.2 is live", "Hotfix v1.04", "Update 2.1").</summary>
    public static string? VersionFromTitle(string title)
    {
        var tagged = TaggedVersion().Match(title);
        if (tagged.Success) return tagged.Groups[1].Value;
        if (!UpdateWord().IsMatch(title)) return null;
        var bare = BareVersion().Match(title);
        return bare.Success ? bare.Groups[1].Value : null;
    }

    /// <summary>
    /// Decides the update state. With versions on both sides it's a straight comparison; otherwise, if the
    /// official build is clearly newer than the installed exe's date, it's "probably" available.
    /// </summary>
    public static (UpdateState state, string detail) Decide(string? installed, string? latest, DateTimeOffset? latestReleased, DateTimeOffset? installedFileDate)
    {
        if (Compare(installed, latest) is { } c)
            return c < 0 ? (UpdateState.Available, $"Version {latest} is out; you have {installed}.")
                         : (UpdateState.UpToDate, $"Up to date ({installed}).");
        if (latestReleased is { } rel && installedFileDate is { } inst)
        {
            if (rel > inst.AddDays(3))
                return (UpdateState.ProbablyAvailable, $"The official game was updated on {rel:d MMM yyyy}; your copy's files date from {inst:d MMM yyyy}." + (latest is null ? "" : $" Latest: {latest}."));
            return (UpdateState.UpToDate, "Your files are as new as the latest official build.");
        }
        return latest is null && latestReleased is null
            ? (UpdateState.CantCheck, "No official version information found for this game.")
            : (UpdateState.Unknown, $"Latest official version: {latest ?? "unknown"}{(latestReleased is { } d ? $" ({d:d MMM yyyy})" : "")}. Set your installed version to compare.");
    }

    [GeneratedRegex(@"\d+(?:\.\d+){0,3}")] private static partial Regex DottedNumber();
    [GeneratedRegex(@"\b(?:v|ver\.?|version|patch|update|hotfix|build)\s*#?\s*(\d+(?:\.\d+){1,3}[a-z]?)\b", RegexOptions.IgnoreCase)] private static partial Regex TaggedVersion();
    [GeneratedRegex(@"\b(patch|update|hotfix|release notes|changelog)\b", RegexOptions.IgnoreCase)] private static partial Regex UpdateWord();
    [GeneratedRegex(@"\b(\d+\.\d+(?:\.\d+){0,2})\b")] private static partial Regex BareVersion();
}
