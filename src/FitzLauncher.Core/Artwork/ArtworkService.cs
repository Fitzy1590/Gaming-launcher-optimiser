using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FitzLauncher.Core.Artwork;

public sealed record ArtworkResult(string CoverPath, string? HeroPath, int? SteamAppId, string MatchedName);

/// <summary>
/// Finds and caches cover art. Steam's public store search works without any key and covers most
/// PC games; SteamGridDB (optional free API key) covers the rest and has community art.
/// </summary>
public sealed class ArtworkService(HttpClient http, string cacheDir, string? steamGridDbKey = null)
{
    public async Task<ArtworkResult?> FindAsync(string gameName, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        try
        {
            var steam = await SearchSteamAsync(gameName, ct);
            if (steam is not null)
            {
                var cover = await DownloadFirstAsync(SteamCoverUrls(steam.Value.id), $"steam_{steam.Value.id}_cover", ct);
                var hero = await DownloadFirstAsync(SteamHeroUrls(steam.Value.id), $"steam_{steam.Value.id}_hero", ct);
                if (cover is null)
                {
                    var header = await SteamHeaderUrlAsync(steam.Value.id, ct);
                    if (header is not null) cover = await DownloadFirstAsync([header], $"steam_{steam.Value.id}_header", ct);
                }
                if (cover is not null) return new ArtworkResult(cover, hero, steam.Value.id, steam.Value.name);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }

        if (!string.IsNullOrWhiteSpace(steamGridDbKey))
        {
            try { return await FromSteamGridDbAsync(gameName, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
        }
        return null;
    }

    public async Task<(int id, string name)?> SearchSteamAsync(string name, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&cc=US&l=english";
        var json = await http.GetStringAsync(url, ct);
        return PickSteamMatch(json, name);
    }

    /// <summary>Picks the best search hit: exact (normalised) name first, otherwise the first app.</summary>
    internal static (int id, string name)? PickSteamMatch(string json, string wanted)
    {
        var result = JsonSerializer.Deserialize<SteamSearch>(json);
        var apps = result?.Items?.Where(i => i.Type == "app").ToList();
        if (apps is null || apps.Count == 0) return null;
        var target = Normalise(wanted);
        var best = apps.FirstOrDefault(a => Normalise(a.Name) == target)
                   ?? apps.FirstOrDefault(a => Normalise(a.Name).StartsWith(target, StringComparison.Ordinal))
                   ?? apps[0];
        return (best.Id, best.Name);
    }

    internal static string Normalise(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    internal static IEnumerable<string> SteamCoverUrls(int id) =>
    [
        $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_600x900_2x.jpg",
        $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/library_600x900_2x.jpg",
        $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_600x900.jpg",
    ];

    internal static IEnumerable<string> SteamHeroUrls(int id) =>
    [
        $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_hero.jpg",
        $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/library_hero.jpg",
    ];

    private async Task<string?> SteamHeaderUrlAsync(int id, CancellationToken ct)
    {
        var json = await http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={id}&filters=basic", ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(id.ToString(), out var app) &&
               app.TryGetProperty("data", out var data) &&
               data.TryGetProperty("header_image", out var header) ? header.GetString() : null;
    }

    private async Task<ArtworkResult?> FromSteamGridDbAsync(string name, CancellationToken ct)
    {
        using var search = new HttpRequestMessage(HttpMethod.Get, $"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(name)}");
        search.Headers.Authorization = new AuthenticationHeaderValue("Bearer", steamGridDbKey);
        using var res = await http.SendAsync(search, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var first = doc.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined) return null;
        var gameId = first.GetProperty("id").GetInt32();
        var matched = first.GetProperty("name").GetString() ?? name;

        async Task<string?> FirstImage(string endpoint, string suffix)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.steamgriddb.com/api/v2/{endpoint}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", steamGridDbKey);
            using var r = await http.SendAsync(req, ct);
            if (!r.IsSuccessStatusCode) return null;
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var url = d.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("url").GetString()).FirstOrDefault();
            return url is null ? null : await DownloadFirstAsync([url], $"sgdb_{gameId}_{suffix}", ct);
        }

        var cover = await FirstImage($"grids/game/{gameId}?dimensions=600x900,342x482,660x930", "cover");
        if (cover is null) return null;
        var hero = await FirstImage($"heroes/game/{gameId}", "hero");
        return new ArtworkResult(cover, hero, null, matched);
    }

    /// <summary>Downloads the first URL that returns an image. Cached by key, so re-adding a game is instant.</summary>
    public async Task<string?> DownloadFirstAsync(IEnumerable<string> urls, string cacheKey, CancellationToken ct)
    {
        foreach (var existing in Directory.EnumerateFiles(cacheDir, cacheKey + ".*")) return existing;
        foreach (var url in urls)
        {
            try
            {
                using var res = await http.GetAsync(url, ct);
                if (!res.IsSuccessStatusCode) continue;
                var type = res.Content.Headers.ContentType?.MediaType ?? "";
                if (!type.StartsWith("image/", StringComparison.Ordinal)) continue;
                var ext = type switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
                var path = Path.Combine(cacheDir, cacheKey + ext);
                await using var fs = File.Create(path);
                await res.Content.CopyToAsync(fs, ct);
                return path;
            }
            catch (HttpRequestException) { }
        }
        return null;
    }

    private sealed class SteamSearch
    {
        [JsonPropertyName("items")] public List<SteamItem>? Items { get; set; }
    }

    private sealed class SteamItem
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("id")] public int Id { get; set; }
    }
}
