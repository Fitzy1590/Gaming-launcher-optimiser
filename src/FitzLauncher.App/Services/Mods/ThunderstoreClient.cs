using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FitzLauncher.Core.Models;

namespace FitzLauncher.App.Services.Mods;

public sealed record ThunderstoreCommunity(string Identifier, string Name);

/// <summary>
/// Thunderstore (no account needed): communities, the package list per community (cached for 6 hours,
/// searched locally), and dependency resolution so a mod installs with BepInExPack and its libraries.
/// </summary>
public sealed class ThunderstoreClient(HttpClient http, string cacheDir)
{
    private readonly Dictionary<string, List<ModSearchResult>> _packages = new(StringComparer.OrdinalIgnoreCase);

    public async Task<List<ThunderstoreCommunity>> CommunitiesAsync(CancellationToken ct = default)
    {
        var cache = Path.Combine(cacheDir, "thunderstore-communities.json");
        if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddDays(-7) && Core.Storage.Json.Load<List<ThunderstoreCommunity>>(cache) is { Count: > 0 } c) return c;
        var list = new List<ThunderstoreCommunity>();
        string? next = "https://thunderstore.io/api/experimental/community/";
        for (var page = 0; next is not null && page < 20; page++)
        {
            var j = JsonNode.Parse(await http.GetStringAsync(next, ct))!;
            foreach (var r in j["results"]?.AsArray() ?? [])
                list.Add(new ThunderstoreCommunity(r!["identifier"]!.GetValue<string>(), r["name"]!.GetValue<string>()));
            next = j["pagination"]?["next_link"]?.GetValue<string>();
        }
        Core.Storage.Json.Save(cache, list);
        return list;
    }

    public static ThunderstoreCommunity? Match(IEnumerable<ThunderstoreCommunity> communities, string gameName)
    {
        static string K(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var key = K(gameName);
        return communities.FirstOrDefault(c => K(c.Name) == key || K(c.Identifier) == key)
            ?? communities.FirstOrDefault(c => K(c.Name).Length > 3 && (key.StartsWith(K(c.Name)) || K(c.Name).StartsWith(key)));
    }

    private sealed class Pkg
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
        [JsonPropertyName("owner")] public string Owner { get; set; } = "";
        [JsonPropertyName("package_url")] public string PackageUrl { get; set; } = "";
        [JsonPropertyName("is_deprecated")] public bool Deprecated { get; set; }
        [JsonPropertyName("rating_score")] public long Rating { get; set; }
        [JsonPropertyName("date_updated")] public DateTimeOffset? Updated { get; set; }
        [JsonPropertyName("categories")] public List<string> Categories { get; set; } = [];
        [JsonPropertyName("versions")] public List<Ver> Versions { get; set; } = [];
    }

    private sealed class Ver
    {
        [JsonPropertyName("version_number")] public string Version { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = [];
        [JsonPropertyName("file_size")] public long FileSize { get; set; }
    }

    /// <summary>Every package in a community (latest version only). Big communities take a few seconds the first time.</summary>
    public async Task<List<ModSearchResult>> PackagesAsync(string community, CancellationToken ct = default)
    {
        if (_packages.TryGetValue(community, out var mem)) return mem;
        var cache = Path.Combine(cacheDir, $"thunderstore-{Core.Mods.ModInstallPlanner.SafeName(community)}.json");
        if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddHours(-6) && Core.Storage.Json.Load<List<ModSearchResult>>(cache) is { } cached)
            return _packages[community] = cached;

        var list = new List<ModSearchResult>();
        await using var stream = await http.GetStreamAsync($"https://thunderstore.io/c/{Uri.EscapeDataString(community)}/api/v1/package/", ct);
        await foreach (var p in JsonSerializer.DeserializeAsyncEnumerable<Pkg>(stream, cancellationToken: ct))
        {
            if (p is null || p.Versions.Count == 0) continue;
            var v = p.Versions[0];
            list.Add(new ModSearchResult
            {
                Source = ModSource.Thunderstore, Id = $"{p.Owner}-{p.Name}", Name = p.Name.Replace('_', ' '), Summary = v.Description, Author = p.Owner,
                Version = v.Version, ImageUrl = v.Icon, Downloads = p.Versions.Sum(x => x.Downloads), Endorsements = p.Rating, PageUrl = p.PackageUrl,
                Updated = p.Updated, DownloadUrl = v.DownloadUrl, FileName = $"{p.FullName}-{v.Version}.zip", SizeBytes = v.FileSize,
                Dependencies = v.Dependencies, Deprecated = p.Deprecated || p.Categories.Contains("Modpacks"),
            });
        }
        Core.Storage.Json.Save(cache, list);
        return _packages[community] = list;
    }

    public async Task<List<ModSearchResult>> SearchAsync(string community, string query, CancellationToken ct = default)
    {
        var all = await PackagesAsync(community, ct);
        var q = query.Trim();
        return all.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Author.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Summary.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Deprecated)
            .ThenByDescending(p => q.Length > 0 && p.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Downloads).Take(60).ToList();
    }

    /// <summary>The package plus everything it needs, dependencies first ("Owner-Name-1.2.3" → package).</summary>
    public async Task<List<ModSearchResult>> WithDependenciesAsync(string community, ModSearchResult package, CancellationToken ct = default)
    {
        var all = (await PackagesAsync(community, ct)).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var order = new List<ModSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(ModSearchResult p)
        {
            if (!seen.Add(p.Id)) return;
            foreach (var dep in p.Dependencies)
                if (all.TryGetValue(Core.Mods.ModCompatibility.DependencyKey(dep), out var d)) Visit(d);
            order.Add(p);
        }
        Visit(package);
        return order;
    }
}
