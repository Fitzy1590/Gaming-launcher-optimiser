using FitzLauncher.Core.Mods;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FitzLauncher.Core.Models;

namespace FitzLauncher.App.Services.Mods;

public sealed record NexusGame(string Domain, string Name, long Id);
public sealed record NexusUser(string Name, bool IsPremium);
/// <summary>nxm://{domain}/mods/{mod}/files/{file}?key=…&amp;expires=…</summary>
public sealed record NxmLink(string Domain, long ModId, long FileId, string? Key, string? Expires)
{
    public static NxmLink? Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase)) return null;
        var seg = u.AbsolutePath.Trim('/').Split('/');
        if (seg.Length < 4 || seg[0] != "mods" || seg[2] != "files" || !long.TryParse(seg[1], out var mod) || !long.TryParse(seg[3], out var file)) return null;
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        return new NxmLink(u.Host, mod, file, q["key"], q["expires"]);
    }
}

/// <summary>
/// Nexus Mods API with your personal API key (nexusmods.com → Site preferences → API keys).
/// v1 REST for game list, mod details, files and download links; v2 GraphQL for search.
/// Free accounts get download links only via the site's "Mod Manager Download" button (nxm:// link);
/// Premium accounts can download straight from the launcher.
/// </summary>
public sealed class NexusClient(HttpClient http, string apiKey, string cacheDir)
{
    private const string V1 = "https://api.nexusmods.com/v1";
    private const string V2 = "https://api.nexusmods.com/v2/graphql";

    private HttpRequestMessage Req(HttpMethod m, string url, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body };
        r.Headers.Add("apikey", apiKey);
        r.Headers.Add("Application-Name", "FitzLauncher");
        r.Headers.Add("Application-Version", UpdateService.CurrentVersion.ToString());
        r.Headers.Accept.ParseAdd("application/json");
        return r;
    }

    private async Task<JsonNode> GetAsync(string url, CancellationToken ct)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Get, url), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new StoreException("Nexus didn't accept the API key. Check it in Settings → Mods.");
        if (res.StatusCode == HttpStatusCode.Forbidden) throw new StoreException(Message(text) ?? "Nexus refused the request (Premium needed for direct downloads).");
        if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new StoreException("Nexus rate limit reached; try again in a few minutes.");
        if (!res.IsSuccessStatusCode) throw new StoreException(Message(text) ?? $"Nexus error {(int)res.StatusCode}.");
        return JsonNode.Parse(text)!;
    }

    private static string? Message(string json)
    {
        try { return JsonNode.Parse(json)?["message"]?.GetValue<string>(); } catch (JsonException) { return null; }
    }

    public async Task<NexusUser> ValidateAsync(CancellationToken ct = default)
    {
        var j = await GetAsync($"{V1}/users/validate.json", ct);
        return new NexusUser(j["name"]?.GetValue<string>() ?? "?", j["is_premium"]?.GetValue<bool>() ?? false);
    }

    /// <summary>All Nexus games (cached for a week).</summary>
    public async Task<List<NexusGame>> GamesAsync(CancellationToken ct = default)
    {
        var cache = Path.Combine(cacheDir, "nexus-games.json");
        if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddDays(-7) && Core.Storage.Json.Load<List<NexusGame>>(cache) is { Count: > 0 } cached) return cached;
        var arr = (await GetAsync($"{V1}/games.json", ct)).AsArray();
        var list = arr.Select(g => new NexusGame(g!["domain_name"]!.GetValue<string>(), g["name"]!.GetValue<string>(), g["id"]!.GetValue<long>())).ToList();
        Core.Storage.Json.Save(cache, list);
        return list;
    }

    public static NexusGame? Match(IEnumerable<NexusGame> games, string gameName)
    {
        static string K(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var key = K(gameName);
        var list = games.ToList();
        return list.FirstOrDefault(g => K(g.Name) == key)
            ?? list.Where(g => K(g.Name).StartsWith(key) || key.StartsWith(K(g.Name))).OrderBy(g => Math.Abs(K(g.Name).Length - key.Length)).FirstOrDefault();
    }

    /// <summary>Search by name (GraphQL v2). An empty query returns the most endorsed mods.</summary>
    public async Task<List<ModSearchResult>> SearchAsync(string domain, string query, CancellationToken ct = default) =>
        (await SearchAsync(domain, query, ModRanking.Relevance, 0, ct)).Results;

    /// <summary>A page of mods for a ranking (most downloaded, popular this month, …), optionally filtered by name.</summary>
    public async Task<(List<ModSearchResult> Results, int Total)> SearchAsync(string domain, string? query, ModRanking ranking, int offset, CancellationToken ct = default)
    {
        const string gql = """
            query Search($filter: ModsFilter, $sort: [ModsSort!], $count: Int, $offset: Int) {
              mods(filter: $filter, sort: $sort, count: $count, offset: $offset) {
                totalCount
                nodes { modId name summary author version pictureUrl thumbnailUrl downloads endorsements updatedAt createdAt status }
              }
            }
            """;
        var (filter, sort) = NexusQuery.Build(domain, query, ranking, DateTimeOffset.UtcNow);
        var j = await GraphAsync(gql, new JsonObject { ["filter"] = filter, ["sort"] = sort, ["count"] = 40, ["offset"] = offset }, ct);
        var mods = j["mods"];
        var list = (mods?["nodes"]?.AsArray() ?? []).Select(n => new ModSearchResult
        {
            Source = ModSource.Nexus,
            Id = n!["modId"]!.ToString(),
            Name = n["name"]?.GetValue<string>() ?? "",
            Summary = WebUtility.HtmlDecode(n["summary"]?.GetValue<string>() ?? ""),
            Author = n["author"]?.GetValue<string>() ?? "",
            Version = n["version"]?.GetValue<string>(),
            ImageUrl = n["thumbnailUrl"]?.GetValue<string>() ?? n["pictureUrl"]?.GetValue<string>(),
            Downloads = n["downloads"]?.GetValue<long>() ?? 0,
            Endorsements = n["endorsements"]?.GetValue<long>() ?? 0,
            Updated = DateTimeOffset.TryParse(n["updatedAt"]?.ToString(), out var u) ? u : null,
            PageUrl = PageUrl(domain, n["modId"]!.ToString()),
            Deprecated = n["status"]?.GetValue<string>() is { } st && !st.Equals("published", StringComparison.OrdinalIgnoreCase),
        }).Where(r => r.Name.Length > 0).ToList();
        return (list, mods?["totalCount"]?.GetValue<int>() ?? list.Count);
    }

    private async Task<JsonNode> GraphAsync(string gql, JsonObject variables, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = gql, ["variables"] = variables };
        using var res = await http.SendAsync(Req(HttpMethod.Post, V2, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new StoreException("Nexus rate limit reached; try again in a few minutes.");
        JsonNode? j;
        try { j = JsonNode.Parse(text); } catch (JsonException) { j = null; }
        if (j?["errors"] is JsonArray { Count: > 0 } || !res.IsSuccessStatusCode || j?["data"] is null)
            throw new StoreException("Nexus didn't answer" + (j?["errors"]?[0]?["message"]?.GetValue<string>() is { } m ? $": {m}" : "") + ". Use Open on Nexus instead.");
        return j["data"]!;
    }

    /// <summary>A mod's requirements: other Nexus mods (same game), external tools, DLC.</summary>
    public async Task<ModRequirementInfo?> RequirementsAsync(long gameId, long modId, CancellationToken ct = default)
    {
        const string gql = """
            query Req($ids: [CompositeIdInput!]!) {
              legacyMods(ids: $ids) {
                nodes {
                  modId name
                  modRequirements {
                    nexusRequirements(count: 50) { nodes { modId modName gameId externalRequirement url notes } }
                    dlcRequirements { gameExpansion { name } }
                  }
                }
              }
            }
            """;
        var j = await GraphAsync(gql, new JsonObject { ["ids"] = new JsonArray(new JsonObject { ["gameId"] = gameId, ["modId"] = modId }) }, ct);
        if (j["legacyMods"]?["nodes"]?[0] is not { } n) return null;
        var reqs = (n["modRequirements"]?["nexusRequirements"]?["nodes"]?.AsArray() ?? []).Select(r =>
        {
            var external = r!["externalRequirement"]?.GetValue<bool>() == true || r["gameId"]?.ToString() != gameId.ToString();
            return new RequiredMod(external ? null : long.TryParse(r["modId"]?.ToString(), out var id) ? id : null,
                r["modName"]?.GetValue<string>() ?? "?", r["url"]?.GetValue<string>(), external, r["notes"]?.GetValue<string>());
        }).ToList();
        var dlc = (n["modRequirements"]?["dlcRequirements"]?.AsArray() ?? []).Select(d => d?["gameExpansion"]?["name"]?.GetValue<string>()).OfType<string>().ToList();
        return new ModRequirementInfo(modId, n["name"]?.GetValue<string>() ?? $"Mod {modId}", reqs, dlc);
    }

    /// <summary>The Nexus game id for a domain (needed by some queries).</summary>
    public async Task<long?> GameIdAsync(string domain, CancellationToken ct = default) =>
        (await GamesAsync(ct)).FirstOrDefault(g => g.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>The mod's main file (newest MAIN, else newest file).</summary>
    public async Task<ModFileOption?> PrimaryFileAsync(string domain, long modId, CancellationToken ct = default) =>
        (await FilesAsync(domain, modId, ct)).FirstOrDefault();

    // ---- your account: tracked mods and endorsements (v1)

    /// <summary>Mods you track on Nexus (all games).</summary>
    public async Task<List<(string Domain, long ModId)>> TrackedAsync(CancellationToken ct = default) =>
        (await GetAsync($"{V1}/user/tracked_mods.json", ct)).AsArray()
            .Select(t => (t?["domain_name"]?.GetValue<string>() ?? "", t?["mod_id"]?.GetValue<long>() ?? 0)).Where(t => t.Item2 > 0).ToList();

    public async Task TrackAsync(string domain, long modId, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Post, $"{V1}/user/tracked_mods.json?domain_name={Uri.EscapeDataString(domain)}",
            new FormUrlEncodedContent([new("mod_id", modId.ToString())])), ct);
        if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.UnprocessableEntity) // 422 = already tracked
            throw new StoreException($"Nexus didn't track the mod ({(int)res.StatusCode}).");
    }

    public async Task EndorseAsync(string domain, long modId, string? version, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Post, $"{V1}/games/{domain}/mods/{modId}/endorse.json",
            new FormUrlEncodedContent([new("version", version ?? "")])), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new StoreException(Message(text) ?? $"Nexus didn't record the endorsement ({(int)res.StatusCode}). You need to have downloaded the mod first.");
    }

    public static string PageUrl(string domain, string modId) => $"https://www.nexusmods.com/{domain}/mods/{modId}";
    public static string FilesUrl(string domain, string modId) => PageUrl(domain, modId) + "?tab=files";

    public async Task<ModSearchResult> ModAsync(string domain, long modId, CancellationToken ct = default)
    {
        var n = await GetAsync($"{V1}/games/{domain}/mods/{modId}.json", ct);
        return new ModSearchResult
        {
            Source = ModSource.Nexus, Id = modId.ToString(), Name = n["name"]?.GetValue<string>() ?? $"Nexus mod {modId}",
            Summary = WebUtility.HtmlDecode(n["summary"]?.GetValue<string>() ?? ""), Author = n["author"]?.GetValue<string>() ?? "",
            Version = n["version"]?.GetValue<string>(), ImageUrl = n["picture_url"]?.GetValue<string>(), PageUrl = PageUrl(domain, modId.ToString()),
        };
    }

    public async Task<List<ModFileOption>> FilesAsync(string domain, long modId, CancellationToken ct = default)
    {
        var j = await GetAsync($"{V1}/games/{domain}/mods/{modId}/files.json", ct);
        return (j["files"]?.AsArray() ?? []).Select(f => new ModFileOption
        {
            FileId = f!["file_id"]!.ToString(),
            Name = f["name"]?.GetValue<string>() ?? "",
            Version = f["version"]?.GetValue<string>(),
            Category = f["category_name"]?.GetValue<string>() ?? "",
            SizeBytes = (f["size_kb"]?.GetValue<long>() ?? 0) * 1024,
            Description = WebUtility.HtmlDecode(f["description"]?.GetValue<string>() ?? ""),
            Uploaded = DateTimeOffset.FromUnixTimeSeconds(f["uploaded_timestamp"]?.GetValue<long>() ?? 0),
            FileName = f["file_name"]?.GetValue<string>(),
        }).Where(f => f.Category is not ("ARCHIVED" or "DELETED" or "REMOVED"))
          .OrderBy(f => f.Category switch { "MAIN" => 0, "UPDATE" => 1, "OPTIONAL" => 2, "MISCELLANEOUS" => 3, _ => 4 }).ThenByDescending(f => f.Uploaded).ToList();
    }

    /// <summary>CDN links for a file. Free accounts need key/expires from an nxm:// link.</summary>
    public async Task<string> DownloadUrlAsync(string domain, long modId, long fileId, string? key = null, string? expires = null, CancellationToken ct = default)
    {
        var url = $"{V1}/games/{domain}/mods/{modId}/files/{fileId}/download_link.json";
        if (key is not null && expires is not null) url += $"?key={Uri.EscapeDataString(key)}&expires={Uri.EscapeDataString(expires)}";
        var arr = (await GetAsync(url, ct)).AsArray();
        return arr.FirstOrDefault()?["URI"]?.GetValue<string>() ?? throw new StoreException("Nexus returned no download link.");
    }
}
