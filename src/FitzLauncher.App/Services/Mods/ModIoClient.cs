using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using FitzLauncher.Core.Models;

namespace FitzLauncher.App.Services.Mods;

public sealed record ModIoGame(long Id, string Name, string NameId);

/// <summary>
/// mod.io with a free read-only API key (mod.io → your account → API access). Note: many mod.io games
/// download and load mods themselves in-game; this installs the files into the game folder for games
/// that load them from there.
/// </summary>
public sealed class ModIoClient(HttpClient http, string apiKey)
{
    private const string Api = "https://api.mod.io/v1";

    private async Task<JsonNode> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var sep = pathAndQuery.Contains('?') ? '&' : '?';
        using var res = await http.GetAsync($"{Api}{pathAndQuery}{sep}api_key={Uri.EscapeDataString(apiKey)}", ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new StoreException("mod.io didn't accept the API key. Check it in Settings → Mods.");
        if (!res.IsSuccessStatusCode) throw new StoreException($"mod.io error {(int)res.StatusCode}: {JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>()}");
        return JsonNode.Parse(text)!;
    }

    public async Task<List<ModIoGame>> FindGamesAsync(string name, CancellationToken ct = default)
    {
        var j = await GetAsync($"/games?_q={Uri.EscapeDataString(name)}&_limit=10", ct);
        return (j["data"]?.AsArray() ?? []).Select(g => new ModIoGame(g!["id"]!.GetValue<long>(), g["name"]!.GetValue<string>(), g["name_id"]?.GetValue<string>() ?? "")).ToList();
    }

    public async Task<List<ModSearchResult>> SearchAsync(long gameId, string query, CancellationToken ct = default)
    {
        var q = string.IsNullOrWhiteSpace(query) ? "" : $"&_q={Uri.EscapeDataString(query.Trim())}";
        JsonNode j;
        try { j = await GetAsync($"/games/{gameId}/mods?_limit=40&_sort=-downloads_total{q}", ct); }
        catch (StoreException) { j = await GetAsync($"/games/{gameId}/mods?_limit=40{q}", ct); }
        return (j["data"]?.AsArray() ?? []).Select(m => Map(m!)).ToList();
    }

    public async Task<ModSearchResult?> ModAsync(long gameId, long modId, CancellationToken ct = default) =>
        Map(await GetAsync($"/games/{gameId}/mods/{modId}", ct));

    public async Task<List<string>> DependenciesAsync(long gameId, long modId, CancellationToken ct = default)
    {
        try
        {
            var j = await GetAsync($"/games/{gameId}/mods/{modId}/dependencies", ct);
            return (j["data"]?.AsArray() ?? []).Select(d => d!["mod_id"]!.ToString()).ToList();
        }
        catch (StoreException) { return []; }
    }

    private static ModSearchResult Map(JsonNode m)
    {
        var file = m["modfile"];
        return new ModSearchResult
        {
            Source = ModSource.ModIo,
            Id = m["id"]!.ToString(),
            Name = m["name"]?.GetValue<string>() ?? "",
            Summary = WebUtility.HtmlDecode(m["summary"]?.GetValue<string>() ?? ""),
            Author = m["submitted_by"]?["username"]?.GetValue<string>() ?? "",
            Version = file?["version"]?.GetValue<string>(),
            ImageUrl = m["logo"]?["thumb_320x180"]?.GetValue<string>(),
            Downloads = m["stats"]?["downloads_total"]?.GetValue<long>() ?? 0,
            Endorsements = m["stats"]?["ratings_positive"]?.GetValue<long>() ?? 0,
            PageUrl = m["profile_url"]?.GetValue<string>() ?? "",
            Updated = m["date_updated"]?.GetValue<long>() is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : null,
            DownloadUrl = file?["download"]?["binary_url"]?.GetValue<string>(),
            FileName = file?["filename"]?.GetValue<string>(),
            SizeBytes = file?["filesize"]?.GetValue<long>(),
        };
    }
}
