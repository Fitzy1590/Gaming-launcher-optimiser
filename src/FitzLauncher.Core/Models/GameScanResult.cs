namespace FitzLauncher.Core.Models;

public enum GameEngine
{
    Unknown,
    Unreal4,
    Unreal5,
    UnrealUnknownVersion,
    Unity,
    Source,
    Source2,
    IdTech,
    RedEngine,
    Frostbite,
    CryEngine,
    Godot,
    ReEngine,
    CreationEngine,
    Decima,
    Anvil,
}

public enum Upscaler { Dlss, DlssFrameGen, Fsr, Xess }

/// <summary>What the file scan learned about an install.</summary>
public sealed class GameScanResult
{
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.Now;
    public GameEngine Engine { get; set; }
    /// <summary>Unreal project name (folder that owns Content\Paks), used to find the user config dir.</summary>
    public string? UnrealProjectName { get; set; }
    /// <summary>Folders that contain the game's editable config (Engine.ini, GameUserSettings.ini, boot.config...).</summary>
    public List<string> ConfigDirectories { get; set; } = [];
    public List<string> ConfigFiles { get; set; } = [];
    public List<Upscaler> Upscalers { get; set; } = [];
    /// <summary>Version of nvngx_dlss.dll, if present.</summary>
    public string? DlssVersion { get; set; }
    /// <summary>Injectors/overlays found in the game folder (ReShade, ENB, SpecialK...).</summary>
    public List<string> Injectors { get; set; } = [];
    public bool UsesVulkan { get; set; }
    public bool UsesDx12 { get; set; }
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    /// <summary>Current values read from known config files (key → value), used to tailor suggestions.</summary>
    public Dictionary<string, string> CurrentSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; set; } = [];
}
