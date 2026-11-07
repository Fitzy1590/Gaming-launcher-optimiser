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

    // ---- organisation & progress
    public List<string> Tags { get; set; } = [];
    public bool Hidden { get; set; }
    public PlayStatus Status { get; set; }
    /// <summary>User-entered length of the main story, used for the progress bar.</summary>
    public double? MainStoryHours { get; set; }
    /// <summary>Manual completion override (0-100). Wins over the hours-based estimate.</summary>
    public int? CompletionPercent { get; set; }

    // ---- performance automation
    public CorePinning CorePinning { get; set; } = CorePinning.Auto;
    public bool ThermalGuard { get; set; } = true;
    public AutoTuneState AutoTune { get; set; } = new();

    // ---- save backups
    public List<string> SaveDirs { get; set; } = [];
    public bool BackupSaves { get; set; } = true;
    public DateTimeOffset? LastSaveBackup { get; set; }
    public string? LastSaveFingerprint { get; set; }

    [JsonIgnore]
    public string ExeName => Path.GetFileName(ExePath);

    /// <summary>0-100 progress: manual value, else hours played vs. main-story length.</summary>
    [JsonIgnore]
    public double? ProgressPercent => CompletionPercent is { } c ? Math.Clamp(c, 0, 100)
        : Status == PlayStatus.Completed ? 100
        : MainStoryHours is > 0 ? Math.Min(100, TotalPlayTime.TotalHours / MainStoryHours.Value * 100) : null;
}

public enum PlayStatus { None, Backlog, Playing, Beaten, Completed, Abandoned }

/// <summary>Which CPU cores the game may run on.</summary>
public enum CorePinning
{
    /// <summary>Pin only where it reliably helps: V-Cache CCD on dual-CCD X3D, P-cores on Intel hybrid.</summary>
    Auto,
    Off,
    PerformanceCores,
    VCacheCcd,
    /// <summary>First CCD only, on CPUs with two equal CCDs (Ryzen 9 / Ryzen AI Max). Test it with a benchmark.</summary>
    SingleCcd,
}

public sealed class AutoTuneState
{
    public bool Enabled { get; set; } = true;
    public bool TargetReached { get; set; }
    public List<AutoTuneStep> Steps { get; set; } = [];
}

public enum AutoTuneOutcome { Pending, Kept, Reverted, Skipped }

/// <summary>One experiment: a suggestion applied after a session that missed the target, judged by the next session.</summary>
public sealed class AutoTuneStep
{
    public string TweakId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.Now;
    public int? TargetFps { get; set; }
    public double BaselineAvg { get; set; }
    public double BaselineLow { get; set; }
    public double BaselineAtTarget { get; set; }
    public double? ResultAvg { get; set; }
    public double? ResultLow { get; set; }
    public double? ResultAtTarget { get; set; }
    public AutoTuneOutcome Outcome { get; set; }
}
