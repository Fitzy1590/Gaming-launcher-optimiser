using System.Text.Json;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Emulation;

/// <summary>IGDB (via Twitch sign-in) game text: description, release date, genres, developer, rating. Request building and answer reading are pure so they can be tested.</summary>
public static partial class Igdb
{
    public const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    public const string GamesUrl = "https://api.igdb.com/v4/games";

    public static string TokenQuery(string clientId, string secret) =>
        $"?client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}&grant_type=client_credentials";

    /// <summary>Apicalypse body searching a title on one platform.</summary>
    public static string SearchBody(string title, int? platformId)
    {
        var clean = title.Replace("\\", " ").Replace("\"", " ").Trim();
        var where = platformId is { } p ? $"where platforms = ({p}); " : "";
        return $"search \"{clean}\"; fields name,summary,first_release_date,genres.name,total_rating,involved_companies.developer,involved_companies.company.name; {where}limit 8;";
    }

    public sealed record Candidate(string Name, GameMeta Meta);

    public static List<Candidate> Parse(string json)
    {
        var list = new List<Candidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var g in doc.RootElement.EnumerateArray())
            {
                if (!g.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;
                var meta = new GameMeta { FetchedAt = DateTimeOffset.Now };
                if (g.TryGetProperty("summary", out var s)) meta.Summary = s.GetString();
                if (g.TryGetProperty("first_release_date", out var d) && d.TryGetInt64(out var unix))
                    meta.Released = DateTimeOffset.FromUnixTimeSeconds(unix).ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
                if (g.TryGetProperty("total_rating", out var r) && r.TryGetDouble(out var rating)) meta.Rating = Math.Round(rating, 0);
                if (g.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
                    meta.Genres = [.. gs.EnumerateArray().Select(x => x.TryGetProperty("name", out var gn) ? gn.GetString() : null).OfType<string>()];
                if (g.TryGetProperty("involved_companies", out var ic) && ic.ValueKind == JsonValueKind.Array)
                    meta.Developer = ic.EnumerateArray()
                        .Where(c => c.TryGetProperty("developer", out var dv) && dv.ValueKind == JsonValueKind.True)
                        .Select(c => c.TryGetProperty("company", out var co) && co.TryGetProperty("name", out var cn) ? cn.GetString() : null)
                        .FirstOrDefault(x => x is not null);
                list.Add(new Candidate(name, meta));
            }
        }
        catch (JsonException) { }
        return list;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    private static string Norm(string s) => NonAlnum().Replace(s.ToLowerInvariant(), " ").Trim();

    /// <summary>The closest name: exact match, else the one that contains (or is contained by) the title, else nothing (a wrong description is worse than none).</summary>
    public static GameMeta? BestMatch(IReadOnlyList<Candidate> candidates, string wanted)
    {
        var w = Norm(wanted);
        if (w.Length == 0) return null;
        var exact = candidates.FirstOrDefault(c => Norm(c.Name) == w);
        if (exact is not null) return exact.Meta;
        var loose = candidates.Where(c => { var n = Norm(c.Name); return n.Contains(w) || w.Contains(n); }).OrderBy(c => Math.Abs(Norm(c.Name).Length - w.Length)).FirstOrDefault();
        return loose?.Meta;
    }
}
