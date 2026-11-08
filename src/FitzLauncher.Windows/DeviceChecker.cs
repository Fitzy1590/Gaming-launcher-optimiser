using System.Security.Principal;
using FitzLauncher.Core.Diagnostics;
using FitzLauncher.Core.Storage;
using Microsoft.Win32;

namespace FitzLauncher.Windows;

/// <summary>
/// Checks the PC for what the launcher (and most games) need: Windows version, 64-bit, admin rights, a writable
/// data folder with space, XInput, the Visual C++ runtimes, the legacy DirectX runtime, .NET Framework 4.8, and
/// the optional tools features rely on (RTSS, Afterburner, RyzenAdj, ViGEmBus, HidHide). The launcher itself is
/// self-contained, so it doesn't need a .NET install.
/// </summary>
public static class DeviceChecker
{
    public static List<DeviceCheckItem> Run(AppPaths paths, bool isApu, string? rtssPath)
    {
        var items = new List<DeviceCheckItem>();
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var wow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

        // ---- required
        var build = Environment.OSVersion.Version.Build;
        items.Add(build >= 19041
            ? new("Windows version", CheckStatus.Ok, $"Windows build {build}.")
            : new("Windows version", CheckStatus.Required, $"Build {build} is too old: the launcher needs Windows 10 version 2004 (build 19041) or newer.", new FixLink("Open Windows Update", "ms-settings:windowsupdate")));
        items.Add(Environment.Is64BitOperatingSystem
            ? new("64-bit Windows", CheckStatus.Ok, "Yes.")
            : new("64-bit Windows", CheckStatus.Required, "The launcher needs 64-bit Windows."));

        try
        {
            Directory.CreateDirectory(paths.Root);
            var probe = Path.Combine(paths.Root, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            var free = new DriveInfo(Path.GetPathRoot(paths.Root)!).AvailableFreeSpace;
            items.Add(free > 2L * 1024 * 1024 * 1024
                ? new("Data folder", CheckStatus.Ok, $"Writable, {free / 1e9:0} GB free.")
                : new("Data folder", CheckStatus.Recommended, $"Only {free / 1e9:0.0} GB free: backups and updates need room.", new FixLink("Free up disk space", "ms-settings:storagesense")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            items.Add(new("Data folder", CheckStatus.Required, $"The launcher can't write to {ErrorExplainer.Redact(paths.Root)}: {e.Message}", new FixLink("Open Windows Security protection history", "windowsdefender://threathistory")));
        }

        // ---- recommended
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        items.Add(admin
            ? new("Administrator rights", CheckStatus.Ok, "Game Mode, FPS tracking, temperatures and power limits can all work.")
            : new("Administrator rights", CheckStatus.Recommended, "Not running as administrator, so Game Mode, FPS tracking and temperatures are limited. Turn on \"Start without the UAC prompt\" in Settings, or right-click → Run as administrator."));

        items.Add(File.Exists(Path.Combine(sys32, "xinput1_4.dll"))
            ? new("Controller support (XInput)", CheckStatus.Ok, "Present.")
            : new("Controller support (XInput)", CheckStatus.Recommended, "xinput1_4.dll is missing, so controllers won't work in the launcher. Windows Update or the DirectX runtime restores it.", new FixLink("Open Windows Update", "ms-settings:windowsupdate")));

        items.Add(VcInstalled("x64")
            ? new("Visual C++ runtime (64-bit)", CheckStatus.Ok, "Installed.")
            : new("Visual C++ runtime (64-bit)", CheckStatus.Recommended, "Most games, RyzenAdj and many tools need it.", new FixLink("Download from Microsoft", ErrorExplainer.VcRedistX64), ErrorExplainer.VcRedistX64));
        items.Add(VcInstalled("x86")
            ? new("Visual C++ runtime (32-bit)", CheckStatus.Ok, "Installed.")
            : new("Visual C++ runtime (32-bit)", CheckStatus.Recommended, "32-bit games and many trainers need it.", new FixLink("Download from Microsoft", ErrorExplainer.VcRedistX86), ErrorExplainer.VcRedistX86));

        var dx = File.Exists(Path.Combine(sys32, "d3dx9_43.dll")) && (!Environment.Is64BitOperatingSystem || File.Exists(Path.Combine(wow64, "d3dx9_43.dll")));
        items.Add(dx
            ? new("DirectX runtime (legacy)", CheckStatus.Ok, "Installed.")
            : new("DirectX runtime (legacy)", CheckStatus.Recommended, "Older games (DirectX 9–11 era) need the June 2010 DirectX runtime; Windows doesn't include it.", new FixLink("Download from Microsoft", "https://www.microsoft.com/download/details.aspx?id=35")));

        var fx = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")?.GetValue("Release") is int rel && rel >= 528040;
        items.Add(fx
            ? new(".NET Framework 4.8", CheckStatus.Ok, "Installed.")
            : new(".NET Framework 4.8", CheckStatus.Recommended, "Some trainers and game tools need it.", new FixLink("Download from Microsoft", "https://dotnet.microsoft.com/download/dotnet-framework/net48")));

        items.Add(new(".NET runtime", CheckStatus.Ok, "Built into the launcher: nothing to install."));

        // ---- optional tools
        items.Add(rtssPath is not null
            ? new("RivaTuner Statistics Server", CheckStatus.Ok, "Found: frame caps and the overlay work.")
            : new("RivaTuner Statistics Server", CheckStatus.Info, "Optional: needed for live FPS caps, the thermal guard and the overlay.", new FixLink("Get RTSS (Guru3D)", "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/")));
        if (isApu)
            items.Add(RyzenAdjClient.Find(null) is not null
                ? new("RyzenAdj", CheckStatus.Ok, "Found: power limits and undervolting work.")
                : new("RyzenAdj", CheckStatus.Info, "Optional: needed for per-game power limits, cooling power trims and undervolting.", new FixLink("Get RyzenAdj (GitHub)", "https://github.com/FlyGoat/RyzenAdj/releases")));
        items.Add(ControllerDevices.ViGEmInstalled() && ControllerDevices.HidHideInstalled()
            ? new("ViGEmBus and HidHide", CheckStatus.Ok, "Installed: per-game controller settings work.")
            : new("ViGEmBus and HidHide", CheckStatus.Info, "Optional: needed for per-game controller deadzones and rumble.", new FixLink("Get ViGEmBus", "https://github.com/nefarius/ViGEmBus/releases")));
        return items;
    }

    private static bool VcInstalled(string arch)
    {
        var key = arch == "x86" ? @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86" : @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue("Installed") is int i && i == 1;
    }
}
