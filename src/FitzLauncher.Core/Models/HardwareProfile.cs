namespace FitzLauncher.Core.Models;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }
public enum DriveKind { Unknown, Hdd, SataSsd, NvmeSsd, SdCard }

/// <summary>Result of the local hardware/OS scan. Drives how tweaks are tailored.</summary>
public sealed class HardwareProfile
{
    /// <summary>Bumped when the scanner learns something new, so cached profiles are refreshed.</summary>
    public const int CurrentScanVersion = 2;
    public int ScanVersion { get; set; } = CurrentScanVersion;
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.Now;

    public string CpuName { get; set; } = "";
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }
    public int CpuMaxClockMhz { get; set; }
    /// <summary>True for CPUs with mixed performance/efficiency cores (Intel 12th gen+).</summary>
    public bool CpuIsHybrid { get; set; }
    /// <summary>True for AMD X3D parts with 3D V-Cache.</summary>
    public bool CpuHasVCache { get; set; }

    public List<GpuInfo> Gpus { get; set; } = [];
    public GpuInfo? PrimaryGpu => Gpus.OrderByDescending(g => g.IsDiscrete).ThenByDescending(g => g.VramBytes).FirstOrDefault();

    public long RamBytes { get; set; }
    public int RamSticks { get; set; }
    public int RamConfiguredMhz { get; set; }
    public int RamRatedMhz { get; set; }
    /// <summary>"DDR4" / "DDR5" / "" when unknown.</summary>
    public string RamType { get; set; } = "";

    public List<DriveInfoLite> Drives { get; set; } = [];
    public List<DisplayInfo> Displays { get; set; } = [];
    public DisplayInfo? PrimaryDisplay => Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays.FirstOrDefault();

    public bool IsLaptop { get; set; }
    public string SystemManufacturer { get; set; } = "";
    public string SystemModel { get; set; } = "";
    /// <summary>Virtual display adapters (emulators, streaming tools) that were skipped as GPUs.</summary>
    public List<string> VirtualDisplayAdapters { get; set; } = [];

    /// <summary>CPU and GPU on one chip sharing power and memory (Ryzen AI Max / Ryzen with Radeon 7xxM-8xxxS, Intel Core Ultra iGPU).</summary>
    public bool IsApu => PrimaryGpu is { IsDiscrete: false };
    public bool OnBattery { get; set; }

    public string OsName { get; set; } = "";
    public int OsBuild { get; set; }
    public bool IsWindows11 => OsBuild >= 22000;

    // Current state of relevant Windows settings (null = unknown).
    public bool? HagsEnabled { get; set; }
    public bool? WindowsGameModeEnabled { get; set; }
    public bool? GameDvrBackgroundRecording { get; set; }
    public bool? MemoryIntegrityEnabled { get; set; }
    public bool? WindowedGameOptimizations { get; set; }
    public string ActivePowerPlan { get; set; } = "";
    public Guid? ActivePowerPlanGuid { get; set; }
    public bool RtssInstalled { get; set; }
    public string? RtssPath { get; set; }

    public double RamGb => RamBytes / 1024d / 1024d / 1024d;
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public GpuVendor Vendor { get; set; }
    public long VramBytes { get; set; }
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public bool IsDiscrete { get; set; }
    public double VramGb => VramBytes / 1024d / 1024d / 1024d;

    /// <summary>
    /// Integrated vs dedicated by name, not memory size: APUs like the Radeon 8060S can reserve 16 GB+
    /// of system RAM as "VRAM". AMD dedicated cards are "Radeon RX"/"Radeon Pro"; Intel's are "Arc A/B".
    /// </summary>
    public static bool LooksDiscrete(GpuVendor vendor, string name) => vendor switch
    {
        GpuVendor.Nvidia => true,
        GpuVendor.Amd => System.Text.RegularExpressions.Regex.IsMatch(name, @"(?i)\bRX\s?\d|Radeon Pro|Radeon VII|Instinct|FirePro"),
        GpuVendor.Intel => System.Text.RegularExpressions.Regex.IsMatch(name, @"(?i)Arc\(TM\)\s?[AB]\d|Arc [AB]\d"),
        _ => false,
    };

    /// <summary>RDNA 3 or newer Radeon: supports AMD Fluid Motion Frames 2 and Anti-Lag 2.</summary>
    public bool IsRdna3OrNewer => Vendor == GpuVendor.Amd &&
        System.Text.RegularExpressions.Regex.IsMatch(Name, @"(?i)RX\s?[79]\d{3}|80[56]0S|8[89]0M|7[68]0M|Radeon\s?8\d{2}M|AI\s?MAX");
}

public sealed class DriveInfoLite
{
    public string Letter { get; set; } = "";
    public DriveKind Kind { get; set; }
    public string Model { get; set; } = "";
    public long FreeBytes { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class DisplayInfo
{
    public string DeviceName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int CurrentRefreshHz { get; set; }
    /// <summary>Highest refresh rate the display supports at its current resolution.</summary>
    public int MaxRefreshHz { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>Identifies a screen by what matters for games, so handheld vs monitor get separate profiles.</summary>
    public string Key => $"{Width}x{Height}@{MaxRefreshHz}";
    public string Label => $"{Width}×{Height} · {MaxRefreshHz} Hz";
}
