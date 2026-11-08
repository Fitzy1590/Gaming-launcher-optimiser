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
    public async Task<List<ModSearchResult>> SearchAsync(string domain, string query, CancellationToken ct = default)
    {
        const string gql = """
            query Search($filter: ModsFilter, $sort: [ModsSort!], $count: Int) {
              mods(filter: $filter, sort: $sort, count: $count) {
                nodes { modId name summary author version pictureUrl thumbnailUrl downloads endorsements updatedAt }
              }
            }
            """;
        var filter = new JsonObject { ["gameDomainName"] = new JsonArray(new JsonObject { ["value"] = domain, ["op"] = "EQUALS" }) };
        if (!string.IsNullOrWhiteSpace(query)) filter["nameStemmed"] = new JsonArray(new JsonObject { ["value"] = query.Trim(), ["op"] = "MATCHES" });
        var body = new JsonObject
        {
            ["query"] = gql,
            ["variables"] = new JsonObject
            {
                ["filter"] = filter, ["count"] = 40,
                ["sort"] = new JsonArray(new JsonObject { [string.IsNullOrWhiteSpace(query) ? "endorsements" : "relevance"] = new JsonObject { ["direction"] = "DESC" } }),
            },
        };
        using var res = await http.SendAsync(Req(HttpMethod.Post, V2, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        var j = JsonNode.Parse(text);
        if (j?["errors"] is JsonArray { Count: > 0 } errs || !res.IsSuccessStatusCode)
            throw new StoreException("Nexus search didn't work" + (j?["errors"]?[0]?["message"]?.GetValue<string>() is { } m ? $": {m}" : "") + ". Use Open on Nexus, then Mod Manager Download.");
        return (j?["data"]?["mods"]?["nodes"]?.AsArray() ?? []).Select(n => new ModSearchResult
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
        }).Where(r => r.Name.Length > 0).ToList();
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
