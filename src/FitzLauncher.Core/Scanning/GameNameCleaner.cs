using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Scanning;

/// <summary>Turns repack/scene folder names into a clean display name.
/// "Elden.Ring.v1.10-[FitGirl Repack]" → "Elden Ring".</summary>
public static partial class GameNameCleaner
{
    private static readonly string[] SceneTags =
    [
        "fitgirl", "dodi", "elamigos", "kaos", "codex", "plaza", "skidrow", "reloaded", "cpy", "empress",
        "gog", "rune", "tenoke", "flt", "razor1911", "goldberg", "repack", "multi\\d*", "steamrip", "rg mechanics",
        "deluxe edition", "goty", "complete edition", "ultimate edition", "all dlcs?", "\\+ ?\\d* ?dlcs?", "dlc",
        "win64", "x64", "portable",
    ];

    public static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = Path.GetFileNameWithoutExtension(raw.Trim());
        s = Regex.Replace(s, @"(?i)-Win(64|32)-Shipping$", "");

        s = Bracketed().Replace(s, " ");                 // [FitGirl Repack], (v1.2)
        s = s.Replace('_', ' ').Replace('.', ' ');
        s = Version().Replace(s, " ");                   // v1.2.3, Build 12345, 1.0.4
        s = Regex.Replace(s, $@"(?i)(?:^|[\s-])(?:{string.Join('|', SceneTags)})(?=$|[\s-])", " ");
        s = s.Replace(" - ", " ").Trim(' ', '-', '+');
        s = Spaces().Replace(s, " ").Trim();
        return s.Length == 0 ? Path.GetFileNameWithoutExtension(raw) : s;
    }

    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"(?i)(?:^|\s)(?:v(?:er(?:sion)?)?\s?\d+(?:[\s.]\d+)*[a-z]?|build\s?\d+|update\s?\d+(?:[\s.]\d+)*|\d+(?:\s\d+){2,})(?=$|\s)")]
    private static partial Regex Version();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
