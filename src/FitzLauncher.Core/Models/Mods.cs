using System.Text.Json.Serialization;

namespace FitzLauncher.Core.Models;

public enum ModSource { Local, Nexus, Thunderstore, ModIo }

/// <summary>A mod installed into one game through the launcher.</summary>
public sealed class InstalledMod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ModSource Source { get; set; }
    /// <summary>Nexus/mod.io mod id, or Thunderstore "Owner-Name".</summary>
    public string? SourceId { get; set; }
    public string? FileId { get; set; }
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? PageUrl { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.Now;
    public bool Enabled { get; set; } = true;
    /// <summary>Install order: later mods win file conflicts.</summary>
    public int Priority { get; set; }
    /// <summary>Game-root-relative paths this mod provides (in its staging folder and, when enabled, in the game).</summary>
    public List<string> Files { get; set; } = [];
    /// <summary>Loaders/frameworks this mod needs (SKSE, BepInEx, UE4SS…), detected from its files.</summary>
    public List<string> Requires { get; set; } = [];
    /// <summary>Other mods it depends on (Thunderstore "Owner-Name", mod.io ids), when the source lists them.</summary>
    public List<string> Dependencies { get; set; } = [];
    /// <summary>Nexus mods this one requires (from its requirements list at install).</summary>
    public List<ModRef> NexusRequirements { get; set; } = [];
    /// <summary>Game exe version when installed, to warn after a game update.</summary>
    public string? GameVersionAtInstall { get; set; }
    /// <summary>How files were placed, for the details view ("FOMOD: picked Core + 2K textures").</summary>
    public string? InstallNotes { get; set; }
    /// <summary>Newest version on the store at the last check, when newer than the installed one.</summary>
    public string? UpdateVersion { get; set; }
    public DateTimeOffset? UpdateCheckedAt { get; set; }

    [JsonIgnore] public string SourceLabel => Source switch { ModSource.Nexus => "Nexus", ModSource.Thunderstore => "Thunderstore", ModSource.ModIo => "mod.io", _ => "Local" };
}

/// <summary>Per-game mod setup: which Nexus/Thunderstore/mod.io game it maps to, and the "find the bad mod" state.</summary>
public sealed class ModSetup
{
    public string? NexusDomain { get; set; }
    public string? ThunderstoreCommunity { get; set; }
    public long? ModIoGameId { get; set; }
    public List<InstalledMod> Mods { get; set; } = [];
    public BisectState? Bisect { get; set; }
}

/// <summary>Binary search for a crashing mod: disable half, play, report, repeat.</summary>
public sealed class BisectState
{
    /// <summary>Mods still under suspicion.</summary>
    public List<Guid> Suspects { get; set; } = [];
    /// <summary>The half currently enabled for the test session.</summary>
    public List<Guid> Testing { get; set; } = [];
    /// <summary>Which mods were enabled before the search started, restored at the end.</summary>
    public List<Guid> OriginallyEnabled { get; set; } = [];
    public int Round { get; set; }
}

/// <summary>A trainer (e.g. FLiNG) set up for one game.</summary>
public sealed class TrainerConfig
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string? PageUrl { get; set; }
    /// <summary>Game versions the trainer says it supports ("v1.0-v1.2.3").</summary>
    public string? SupportedVersions { get; set; }
    public bool LaunchWithGame { get; set; } = true;
    /// <summary>Seconds after the game starts before the trainer is opened (it attaches to the running game).</summary>
    public int DelaySeconds { get; set; } = 10;
    public bool CloseWithGame { get; set; } = true;
    /// <summary>Seconds to let the trainer find the game before its options are switched on.</summary>
    public int ApplyDelaySeconds { get; set; } = 3;
    /// <summary>After switching your options on, go back to the game (otherwise the trainer stays in front).</summary>
    public bool ReturnToGameAfterApply { get; set; } = true;
    /// <summary>The trainer's cheats and hotkeys; the ones ticked are "your usual" shown at launch.</summary>
    public List<TrainerCheat> Cheats { get; set; } = [];
    public string? Notes { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class TrainerCheat
{
    public string Hotkey { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Favourite { get; set; }
    /// <summary>Press this option's hotkey for you at launch, once the trainer has attached to the game (on/off options only).</summary>
    public bool OnAtLaunch { get; set; }
    /// <summary>Added by hand rather than read from the FLiNG page (kept when the list is refreshed).</summary>
    public bool Manual { get; set; }
}

/// <summary>A reference to another mod by its Nexus id.</summary>
public sealed record ModRef(long ModId, string Name);
