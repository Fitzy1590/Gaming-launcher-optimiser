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
    public bool UsesDx9 { get; set; }
    public bool UsesOpenGl { get; set; }
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    /// <summary>Current values read from known config files (key → value), used to tailor suggestions.</summary>
    public Dictionary<string, string> CurrentSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; set; } = [];

    /// <summary>Steam app id found in steam_appid.txt / emulator config (repacks usually ship one).</summary>
    public int? SteamAppId { get; set; }
    /// <summary>Unity company/product from app.info: locates saves in AppData\LocalLow.</summary>
    public string? UnityCompany { get; set; }
    public string? UnityProduct { get; set; }
    /// <summary>The exe that actually renders (Unreal: *-Win64-Shipping.exe), which RTSS profiles and GPU preferences must target.</summary>
    public string? RenderExePath { get; set; }
    /// <summary>Low-latency tech compiled into the game (from DLLs and exe strings).</summary>
    public bool SupportsReflex { get; set; }
    public bool SupportsAntiLag2 { get; set; }
    /// <summary>Swappable upscaler DLLs with their versions.</summary>
    public List<UpscalerDllInfo> UpscalerDlls { get; set; } = [];
    /// <summary>Bundled runtime installers (_CommonRedist etc.).</summary>
    public List<string> RedistInstallers { get; set; } = [];
}

public enum UpscalerDllKind { DlssSuperResolution, DlssFrameGeneration, DlssRayReconstruction, XeSS, FsrDx12, FsrVulkan }

public sealed class UpscalerDllInfo
{
    public string Path { get; set; } = "";
    public UpscalerDllKind Kind { get; set; }
    public string Version { get; set; } = "";
}
