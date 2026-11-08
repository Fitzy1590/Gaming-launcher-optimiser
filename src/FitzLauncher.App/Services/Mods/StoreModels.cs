using FitzLauncher.Core.Models;

namespace FitzLauncher.App.Services.Mods;

/// <summary>A mod from any store, as shown in Find mods.</summary>
public sealed class ModSearchResult
{
    public ModSource Source { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public string? Version { get; init; }
    public string? ImageUrl { get; init; }
    public long Downloads { get; init; }
    public long Endorsements { get; init; }
    public string PageUrl { get; init; } = "";
    public DateTimeOffset? Updated { get; init; }
    /// <summary>Direct download (Thunderstore / mod.io); null for Nexus (files are listed separately).</summary>
    public string? DownloadUrl { get; init; }
    public string? FileName { get; init; }
    public long? SizeBytes { get; init; }
    public List<string> Dependencies { get; init; } = [];
    public bool Deprecated { get; init; }
}

public sealed class ModFileOption
{
    public string FileId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Version { get; init; }
    public string Category { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Description { get; init; } = "";
    public DateTimeOffset Uploaded { get; init; }
    public string? FileName { get; init; }
    public string Label => $"{Name} {Version} · {Category} · {SizeBytes / 1024d / 1024:0.#} MB";
}

public sealed class StoreException(string message) : Exception(message);
