using System.Net;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Trainers;

public sealed record TrainerSearchResult(string Title, string Url);
public sealed record TrainerDownload(string Label, string Url, string? Versions);
public sealed record TrainerPage(string Title, List<TrainerDownload> Downloads, List<TrainerCheat> Cheats, string? Notes);

public enum VersionMatch { Unknown, Likely, Mismatch }

/// <summary>
/// Reads FLiNG's public pages (flingtrainer.com, and archive.flingtrainer.com for older trainers): search
/// results, a trainer's download links with the game versions each supports, and its cheat/hotkey list.
/// HTML is parsed loosely so small site changes don't break it.
/// </summary>
public static partial class FlingParser
{
    public const string SearchUrl = "https://flingtrainer.com/?s=";

    public static List<TrainerSearchResult> ParseSearch(string html)
    {
        var results = new List<TrainerSearchResult>();
        foreach (Match m in Anchor().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
            var text = Clean(m.Groups["text"].Value);
            if (!href.Contains("flingtrainer.com/trainer/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!text.Contains("Trainer", StringComparison.OrdinalIgnoreCase) || text.Length > 120) continue;
            if (results.Any(r => r.Url.Equals(href, StringComparison.OrdinalIgnoreCase))) continue;
            results.Add(new TrainerSearchResult(text, href));
        }
        return results;
    }

    public static TrainerPage ParsePage(string html, string pageUrl)
    {
        var title = Clean(TitleTag().Match(html).Groups[1].Value).Split(" – ")[0].Split(" | ")[0].Trim();
        var downloads = new List<TrainerDownload>();
        foreach (Match m in Anchor().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
            var text = Clean(m.Groups["text"].Value);
            var isFile = ArchiveExt().IsMatch(href) || href.Contains("/downloads/", StringComparison.OrdinalIgnoreCase) || href.Contains("download-attachment", StringComparison.OrdinalIgnoreCase) || href.Contains("?download=", StringComparison.OrdinalIgnoreCase);
            if (!isFile || !IsFlingHost(href)) continue;
            if (downloads.Any(d => d.Url.Equals(href, StringComparison.OrdinalIgnoreCase))) continue;
            var label = text.Length > 0 ? text : Path.GetFileName(new Uri(href).AbsolutePath);
            downloads.Add(new TrainerDownload(label, href, VersionRange(label)));
        }

        var text2 = Clean(BreakTags().Replace(html, "\n"));
        var cheats = new List<TrainerCheat>();
        foreach (var raw in text2.Split('\n'))
        {
            var line = raw.Trim();
            var cm = CheatLine().Match(line);
            if (!cm.Success) continue;
            var hotkey = Regex.Replace(cm.Groups["key"].Value, @"\s+", " ").Trim();
            var name = cm.Groups["name"].Value.Trim();
            if (name.Length is 0 or > 80 || cheats.Any(c => c.Hotkey == hotkey && c.Name == name)) continue;
            cheats.Add(new TrainerCheat { Hotkey = hotkey, Name = name });
        }
        var notes = text2.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Notes", StringComparison.OrdinalIgnoreCase) && l.Length < 400);
        return new TrainerPage(title.Length > 0 ? title : pageUrl, downloads, cheats, notes);
    }

    /// <summary>"Elden Ring v1.02-v1.10 Plus 24 Trainer" → "v1.02-v1.10".</summary>
    public static string? VersionRange(string label)
    {
        var m = VersionText().Match(label);
        return m.Success ? m.Value.Trim() : null;
    }

    /// <summary>Is the game's version inside the trainer's stated range (or equal to its single version / "+" lower bound)?</summary>
    public static VersionMatch Check(string? trainerRange, string? gameVersion)
    {
        if (trainerRange is null || gameVersion is null) return VersionMatch.Unknown;
        var parts = Regex.Matches(trainerRange, @"\d+(?:\.\d+)*").Select(m => Parse(m.Value)).Where(v => v is not null).Select(v => v!).ToList();
        var game = Parse(Regex.Match(gameVersion, @"\d+(?:\.\d+)*").Value);
        if (parts.Count == 0 || game is null) return VersionMatch.Unknown;
        var lo = parts[0];
        var hi = parts.Count > 1 ? parts[^1] : trainerRange.Contains('+') ? new Version(int.MaxValue, 0) : parts[0];
        // Compare at the precision the trainer gives ("v1.2" matches 1.2.0.12345).
        return Cmp(game, lo) >= 0 && Cmp(game, hi) <= 0 ? VersionMatch.Likely : VersionMatch.Mismatch;

        static int Cmp(Version a, Version b)
        {
            int[] pa = [a.Major, Math.Max(a.Minor, 0), Math.Max(a.Build, 0)], pb = [b.Major, Math.Max(b.Minor, 0), Math.Max(b.Build, 0)];
            var depth = b.Build >= 0 ? 3 : b.Minor >= 0 ? 2 : 1;
            for (var i = 0; i < depth; i++) if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            return 0;
        }
    }

    /// <summary>A repack's version from its folder name ("Game v1.2.3 [FitGirl]"), when the exe has none.</summary>
    public static string? VersionFromName(string name)
    {
        var m = Regex.Match(name, @"\bv\s?(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static Version? Parse(string s)
    {
        var nums = s.Split('.').Take(4).Select(p => int.TryParse(p, out var n) ? n : -1).ToArray();
        if (nums.Length == 0 || nums[0] < 0) return null;
        return nums.Length switch { 1 => new Version(nums[0], 0), 2 => new Version(nums[0], nums[1]), _ => new Version(nums[0], nums[1], nums[2]) };
    }

    private static bool IsFlingHost(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("flingtrainer.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".flingtrainer.com", StringComparison.OrdinalIgnoreCase));

    public static bool IsTrustedUrl(string url) => IsFlingHost(url);

    private static string Clean(string html) => WebUtility.HtmlDecode(Tags().Replace(html, "")).Replace(' ', ' ').Trim();

    [GeneratedRegex(@"<a\s[^>]*href=""(?<href>[^""]+)""[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex Anchor();
    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex TitleTag();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"<(?:br|/p|/li|/div|/h\d|/tr)\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex BreakTags();
    [GeneratedRegex(@"\.(zip|rar|7z)(\?|$)", RegexOptions.IgnoreCase)] private static partial Regex ArchiveExt();
    [GeneratedRegex(@"v\s?\d+(?:\.\d+)*(?:\s*[-–~]\s*v?\s?\d+(?:\.\d+)*)?\+?", RegexOptions.IgnoreCase)] private static partial Regex VersionText();
    [GeneratedRegex(@"^(?<key>(?:(?:Ctrl|Alt|Shift)\s*\+\s*)*(?:Num(?:pad)?\s*[0-9.+\-*/]|F\d{1,2}|Home|End|Insert|Delete|PageUp|PageDown|Page Up|Page Down))\s*[–\-:]\s*(?<name>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CheatLine();
}


/// <summary>Keeping your ticks when a trainer's option list is refreshed or you add options by hand.</summary>
public static class TrainerOptions
{
    private static string Key(TrainerCheat c) => (c.Hotkey.Replace(" ", "") + "|" + c.Name).ToLowerInvariant();

    /// <summary>
    /// The freshly read list, carrying over Favourite and On-at-launch ticks (matched by hotkey and name, or by hotkey alone when the
    /// name was reworded), plus the options you added by hand that the page doesn't list.
    /// </summary>
    public static List<TrainerCheat> Merge(IReadOnlyList<TrainerCheat> existing, IReadOnlyList<TrainerCheat> fresh)
    {
        var result = new List<TrainerCheat>();
        foreach (var f in fresh)
        {
            var old = existing.FirstOrDefault(e => Key(e) == Key(f)) ?? existing.FirstOrDefault(e => e.Hotkey.Replace(" ", "").Equals(f.Hotkey.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            result.Add(new TrainerCheat { Hotkey = f.Hotkey, Name = f.Name, Favourite = old?.Favourite == true, OnAtLaunch = old?.OnAtLaunch == true });
        }
        foreach (var m in existing.Where(e => e.Manual && result.All(r => Key(r) != Key(e)))) result.Add(m);
        return result;
    }

    /// <summary>Adds an option by hand. Returns an error message, or null when it was added.</summary>
    public static string? AddManual(List<TrainerCheat> list, string hotkey, string name)
    {
        hotkey = System.Text.RegularExpressions.Regex.Replace(hotkey.Trim(), @"\s+", " ");
        name = name.Trim();
        if (name.Length == 0) return "Give the option a name (for example Infinite Stamina).";
        if (Input.HotkeyParser.Parse(hotkey) is not { } chord) return $"\"{hotkey}\" isn't a hotkey Fitz can press. Use the form the trainer shows, like Num 2 or Ctrl+Num 1.";
        if (Input.HotkeyParser.IsTrainerMasterSwitch(chord)) return "Ctrl+Shift+Home switches all of the trainer's hotkeys off, so it can't be added.";
        if (list.Any(c => Key(c) == (hotkey.Replace(" ", "") + "|" + name).ToLowerInvariant())) return "That option is already in the list.";
        list.Add(new TrainerCheat { Hotkey = hotkey, Name = name, Manual = true, OnAtLaunch = true });
        return null;
    }
}
