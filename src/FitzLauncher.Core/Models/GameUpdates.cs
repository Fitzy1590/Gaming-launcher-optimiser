namespace FitzLauncher.Core.Models;

public enum UpdateState { Unknown, UpToDate, Available, ProbablyAvailable, CantCheck }

/// <summary>What the official sources (Steam, GOG) say about the newest version, versus what's installed.</summary>
public sealed class GameUpdateInfo
{
    /// <summary>Version the user says is installed, overriding detection (repacks often have no version in the exe).</summary>
    public string? InstalledVersionOverride { get; set; }
    public string? InstalledVersion { get; set; }
    public string? LatestVersion { get; set; }
    public DateTimeOffset? LatestReleased { get; set; }
    public string? Source { get; set; }
    public long? GogProductId { get; set; }
    public string? PatchNotesTitle { get; set; }
    public string? PatchNotesUrl { get; set; }
    public string? StoreUrl { get; set; }
    public UpdateState State { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public List<GameUpdateRecord> History { get; set; } = [];
}

/// <summary>One update applied through the launcher, with its backup for rollback.</summary>
public sealed class GameUpdateRecord
{
    public DateTimeOffset When { get; set; } = DateTimeOffset.Now;
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public string SourceFile { get; set; } = "";
    public string BackupDir { get; set; } = "";
    public bool FullBackup { get; set; }
    public bool Success { get; set; }
    public bool RolledBack { get; set; }
    public string Notes { get; set; } = "";
}
