using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Models;
using Microsoft.Win32;

namespace FitzLauncher.Windows;

/// <summary>
/// One-off local scan of CPU, GPU, RAM, storage, displays and the Windows settings that matter for
/// gaming. Takes ~1-2 s (mostly WMI), runs off the UI thread, and the result is cached to disk.
/// </summary>
public static partial class HardwareScanner
{
    public static HardwareProfile Scan()
    {
        var hw = new HardwareProfile();
        Try(() => ScanCpu(hw));
        Try(() => ScanGpus(hw));
        Try(() => ScanRam(hw));
        Try(() => ScanSystem(hw));
        Try(() => ScanDrives(hw));
        Try(() => ScanDisplays(hw));
        Try(() => ScanOs(hw));
        Try(() => ScanSettings(hw));
        Try(() => FindRtss(hw));
        return hw;
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException or FormatException or System.Security.SecurityException)
        {
            Debug.WriteLine(e);
        }
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        foreach (var o in searcher.Get()) yield return o;
    }

    private static void ScanCpu(HardwareProfile hw)
    {
        foreach (var cpu in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            hw.CpuName = (cpu["Name"]?.ToString() ?? "").Trim();
            hw.CpuCores += Convert.ToInt32(cpu["NumberOfCores"] ?? 0);
            hw.CpuThreads += Convert.ToInt32(cpu["NumberOfLogicalProcessors"] ?? 0);
            hw.CpuMaxClockMhz = Convert.ToInt32(cpu["MaxClockSpeed"] ?? 0);
        }
        hw.CpuIsHybrid = HybridCpu().IsMatch(hw.CpuName);
        hw.CpuHasVCache = hw.CpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"(?i)(1[2-4]th Gen Intel|Core\(TM\) Ultra|Core Ultra)")]
    private static partial Regex HybridCpu();

    private static void ScanGpus(HardwareProfile hw)
    {
        var vramByName = ReadVramFromRegistry();
        foreach (var vc in Query("SELECT Name, DriverVersion, DriverDate, PNPDeviceID, AdapterRAM FROM Win32_VideoController"))
        {
            var name = vc["Name"]?.ToString() ?? "";
            if (Regex.IsMatch(name, "(?i)basic display|virtual|remote|parsec|idd|mirage|citrix"))
            {
                if (!name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) hw.VirtualDisplayAdapters.Add(name);
                continue;
            }
            var pnp = vc["PNPDeviceID"]?.ToString() ?? "";
            var vendor = pnp.Contains("VEN_10DE") || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
                       : pnp.Contains("VEN_1002") || name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd
                       : pnp.Contains("VEN_8086") || name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
                       : GpuVendor.Unknown;
            // AdapterRAM is a uint32 and caps at 4 GB, so prefer the driver's 64-bit value from the registry.
            var vram = vramByName.TryGetValue(name, out var v) ? v : Convert.ToInt64(vc["AdapterRAM"] ?? 0L);
            DateTime? driverDate = null;
            if (vc["DriverDate"]?.ToString() is { Length: > 0 } d) driverDate = ManagementDateTimeConverter.ToDateTime(d);
            hw.Gpus.Add(new GpuInfo
            {
                Name = name,
                Vendor = vendor,
                VramBytes = vram,
                DriverVersion = vc["DriverVersion"]?.ToString() ?? "",
                DriverDate = driverDate,
                IsDiscrete = GpuInfo.LooksDiscrete(vendor, name),
            });
        }
    }

    private static Dictionary<string, long> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return result;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc")?.ToString();
                var mem = k?.GetValue("HardwareInformation.qwMemorySize");
                if (desc is null || mem is null) continue;
                var bytes = mem switch { long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), _ => Convert.ToInt64(mem) };
                result[desc] = bytes;
            }
            catch (System.Security.SecurityException) { }
        }
        return result;
    }

    private static void ScanRam(HardwareProfile hw)
    {
        foreach (var m in Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            hw.RamSticks++;
            hw.RamBytes += Convert.ToInt64(m["Capacity"] ?? 0L);
            hw.RamRatedMhz = Math.Max(hw.RamRatedMhz, Convert.ToInt32(m["Speed"] ?? 0));
            var configured = Convert.ToInt32(m["ConfiguredClockSpeed"] ?? 0);
            hw.RamConfiguredMhz = hw.RamConfiguredMhz == 0 ? configured : Math.Min(hw.RamConfiguredMhz, configured);
            hw.RamType = Convert.ToInt32(m["SMBIOSMemoryType"] ?? 0) switch { 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 30 or 35 => "LPDDR", _ => hw.RamType };
        }
        if (hw.RamBytes == 0)
        {
            var status = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
            if (Native.GlobalMemoryStatusEx(ref status)) hw.RamBytes = (long)status.TotalPhys;
        }
    }

    private static void ScanSystem(HardwareProfile hw)
    {
        foreach (var cs in Query("SELECT PCSystemType, Manufacturer, Model FROM Win32_ComputerSystem"))
        {
            if (Convert.ToInt32(cs["PCSystemType"] ?? 0) == 2) hw.IsLaptop = true;
            hw.SystemManufacturer = cs["Manufacturer"]?.ToString()?.Trim() ?? "";
            hw.SystemModel = cs["Model"]?.ToString()?.Trim() ?? "";
        }
        if (Query("SELECT Name FROM Win32_Battery").Any()) hw.IsLaptop = true;
        if (Native.GetSystemPowerStatus(out var ps)) hw.OnBattery = ps.ACLineStatus == 0;
    }

    private static void ScanDrives(HardwareProfile hw)
    {
        const string storage = @"root\Microsoft\Windows\Storage";
        var kinds = new Dictionary<string, (DriveKind kind, string model)>();
        foreach (var d in Query("SELECT DeviceId, MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk", storage))
        {
            var media = Convert.ToInt32(d["MediaType"] ?? 0);
            var bus = Convert.ToInt32(d["BusType"] ?? 0);
            var friendly = d["FriendlyName"]?.ToString() ?? "";
            // SD/microSD: bus type SD (12) / MMC (13), or a card reader that reports as SCSI.
            var kind = bus is 12 or 13 || friendly.Contains("Card Reader", StringComparison.OrdinalIgnoreCase) ? DriveKind.SdCard
                     : bus == 17 ? DriveKind.NvmeSsd
                     : media switch { 3 => DriveKind.Hdd, 4 => DriveKind.SataSsd, _ => DriveKind.Unknown };
            kinds[d["DeviceId"]?.ToString() ?? ""] = (kind, d["FriendlyName"]?.ToString() ?? "");
        }
        foreach (var p in Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", storage))
        {
            var letterRaw = p["DriveLetter"];
            if (letterRaw is null) continue;
            var letter = Convert.ToChar(letterRaw);
            if (letter == '\0' || !char.IsLetter(letter)) continue;
            var disk = p["DiskNumber"]?.ToString() ?? "";
            kinds.TryGetValue(disk, out var info);
            var di = new DriveInfo(letter + ":\\");
            hw.Drives.Add(new DriveInfoLite
            {
                Letter = letter + ":",
                Kind = info.kind,
                Model = info.model ?? "",
                FreeBytes = di.IsReady ? di.AvailableFreeSpace : 0,
                TotalBytes = di.IsReady ? di.TotalSize : 0,
            });
        }
    }

    /// <summary>Displays right now (cheap). Used at every launch, so docking/undocking is picked up.</summary>
    public static List<DisplayInfo> CurrentDisplays()
    {
        var hw = new HardwareProfile();
        Try(() => ScanDisplays(hw));
        return hw.Displays;
    }

    private static void ScanDisplays(HardwareProfile hw)
    {
        var dd = new Native.DisplayDevice { cb = Marshal.SizeOf<Native.DisplayDevice>() };
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<Native.DisplayDevice>())
        {
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var current = new Native.DevMode { dmSize = (short)Marshal.SizeOf<Native.DevMode>() };
            if (!Native.EnumDisplaySettings(dd.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref current)) continue;
            var max = current.dmDisplayFrequency;
            var mode = new Native.DevMode { dmSize = (short)Marshal.SizeOf<Native.DevMode>() };
            for (var m = 0; Native.EnumDisplaySettings(dd.DeviceName, m, ref mode); m++)
            {
                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight)
                    max = Math.Max(max, mode.dmDisplayFrequency);
            }
            hw.Displays.Add(new DisplayInfo
            {
                DeviceName = dd.DeviceName.Replace(@"\\.\", ""),
                Width = current.dmPelsWidth,
                Height = current.dmPelsHeight,
                CurrentRefreshHz = current.dmDisplayFrequency,
                MaxRefreshHz = max,
                IsPrimary = (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
            });
        }
    }

    private static void ScanOs(HardwareProfile hw)
    {
        const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        _ = int.TryParse(WindowsRegistryAccess.Read(Registry.LocalMachine, nt, "CurrentBuildNumber")?.ToString(), out var build);
        hw.OsBuild = build;
        var display = WindowsRegistryAccess.Read(Registry.LocalMachine, nt, "DisplayVersion")?.ToString() ?? "";
        // ProductName still says "Windows 10" on Windows 11, so derive it from the build number.
        hw.OsName = $"{(build >= 22000 ? "Windows 11" : "Windows 10")} {display}".Trim();
    }

    private static void ScanSettings(HardwareProfile hw)
    {
        var hags = WindowsRegistryAccess.Read(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        hw.HagsEnabled = hags is int h ? h == 2 : null;

        var gameMode = WindowsRegistryAccess.Read(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        hw.WindowsGameModeEnabled = gameMode is int g ? g != 0 : true; // default is on

        var historical = WindowsRegistryAccess.Read(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled");
        hw.GameDvrBackgroundRecording = historical is int hc && hc == 1;

        var hvci = WindowsRegistryAccess.Read(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        hw.MemoryIntegrityEnabled = hvci is int m ? m == 1 : null;

        var global = WindowsRegistryAccess.Read(Registry.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings")?.ToString() ?? "";
        hw.WindowedGameOptimizations = global.Contains("SwapEffectUpgradeEnable=1");

        var (guid, name) = PowerPlans.GetActive();
        hw.ActivePowerPlanGuid = guid;
        hw.ActivePowerPlan = name;
    }

    private static void FindRtss(HardwareProfile hw)
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server"),
        };
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app?.GetValue("DisplayName")?.ToString()?.Contains("RivaTuner Statistics Server") == true &&
                    app.GetValue("InstallLocation")?.ToString() is { Length: > 0 } loc)
                    candidates.Insert(0, loc);
            }
        }
        var found = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "RTSS.exe")));
        hw.RtssInstalled = found is not null;
        hw.RtssPath = found;
    }
}
