using System.Text.Json.Serialization;

namespace FitzLauncher.Core.Models;

/// <summary>A game (or repack) the user has added to the library.</summary>
public sealed class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    /// <summary>Root folder of the install. Defaults to the exe's folder, but is widened to the
    /// real game root when the exe lives in e.g. Binaries\Win64.</summary>
    public string InstallDir { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>Extra launch options added by optimisations (kept separate so they can be reverted).</summary>
    public string OptimizerArguments { get; set; } = "";
    /// <summary>User-chosen folder holding the game's config files, when auto-detection can't find it.</summary>
    public string? CustomConfigDir { get; set; }
    /// <summary>Local path of the cached cover art (portrait 2:3), if any.</summary>
    public string? ArtworkPath { get; set; }
    /// <summary>Optional wide hero/banner image for the details pane.</summary>
    public string? HeroPath { get; set; }
    public int? SteamAppId { get; set; }

    /// <summary>FPS target chosen for this game (90/100/110/117), or null for "uncapped".</summary>
    public int? FpsTarget { get; set; }
    public bool UseGameMode { get; set; } = true;
    public bool Favourite { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastPlayed { get; set; }
    public TimeSpan TotalPlayTime { get; set; }

    public GameScanResult? LastScan { get; set; }
    public DateTimeOffset? OptimizedAt { get; set; }
    /// <summary>Every change the launcher made for this game, newest last, so it can be reverted.</summary>
    public List<AppliedChange> AppliedChanges { get; set; } = [];

    /// <summary>Summary of the most recent session, for the library card badge.</summary>
    public SessionSummary? LastSession { get; set; }

    [JsonIgnore]
    public string ExeName => Path.GetFileName(ExePath);
}
