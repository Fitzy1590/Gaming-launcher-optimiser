using System.Diagnostics;
using FitzLauncher.Core.Runtimes;
using Microsoft.Win32;

namespace FitzLauncher.Windows;

/// <summary>Checks whether a repack's bundled runtimes are installed, and installs the missing ones silently.</summary>
public sealed class RedistService : IRedistProbe
{
    private readonly Lazy<List<string>> _installed = new(ReadUninstallNames);

    public static RedistInfo? Classify(string path)
    {
        string? product = null, version = null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            product = vi.ProductName ?? vi.FileDescription;
            version = vi.FileVersion?.Replace(',', '.').Replace(" ", "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return RedistCatalog.Classify(path, product, version);
    }

    public bool IsInstalled(RedistInfo r)
    {
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var wow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        switch (r.Kind)
        {
            case RedistKind.VcRedist when r.Year == "2015":
            {
                var key = r.Arch == "x86"
                    ? @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86"
                    : @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";
                using var k = Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("Installed") is not int installed || installed != 1) return false;
                // The installed runtime must be at least as new as the one the game ships.
                var have = (k.GetValue("Version") as string)?.TrimStart('v');
                return !Version.TryParse(have, out var hv) || !Version.TryParse(r.Version, out var need) ||
                       new Version(hv.Major, hv.Minor) >= new Version(need.Major, need.Minor);
            }
            case RedistKind.VcRedist:
                return _installed.Value.Any(n => n.Contains($"Visual C++ {r.Year}", StringComparison.OrdinalIgnoreCase) &&
                                                 n.Contains(r.Arch ?? "x86", StringComparison.OrdinalIgnoreCase));
            case RedistKind.DirectX:
                return File.Exists(Path.Combine(sys32, "d3dx9_43.dll")) && File.Exists(Path.Combine(sys32, "xinput1_3.dll")) &&
                       (!Environment.Is64BitOperatingSystem || File.Exists(Path.Combine(wow64, "d3dx9_43.dll")));
            case RedistKind.DotNetFramework:
                if (r.DisplayName.Contains("3.5"))
                    return Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5")?.GetValue("Install") is 1;
                return Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")?.GetValue("Release") is int rel && rel >= 461808;
            case RedistKind.PhysX:
                return _installed.Value.Any(n => n.Contains("PhysX", StringComparison.OrdinalIgnoreCase));
            case RedistKind.OpenAL:
                return File.Exists(Path.Combine(sys32, "OpenAL32.dll")) || File.Exists(Path.Combine(wow64, "OpenAL32.dll"));
            case RedistKind.Xna:
                return _installed.Value.Any(n => n.Contains("XNA Framework", StringComparison.OrdinalIgnoreCase));
            default:
                return false;
        }
    }

    /// <summary>Runs one installer and waits. Exit codes 0, 1638 (newer present) and 3010 (reboot later) count as success.</summary>
    public static async Task<(bool ok, string message)> InstallAsync(RedistInfo r, CancellationToken ct = default)
    {
        var isMsi = r.Path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        var psi = isMsi
            ? new ProcessStartInfo("msiexec.exe", $"/i \"{r.Path}\" {r.SilentArgs ?? "/qn"} /norestart")
            : new ProcessStartInfo(r.Path, r.SilentArgs ?? "");
        psi.UseShellExecute = true;
        psi.WorkingDirectory = Path.GetDirectoryName(r.Path)!;
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, "Installer did not start.");
            await p.WaitForExitAsync(ct);
            return p.ExitCode switch
            {
                0 or 1638 or 1641 => (true, "Installed"),
                3010 => (true, "Installed (restart Windows when convenient)"),
                _ => (false, $"Installer exited with code {p.ExitCode}"),
            };
        }
        catch (System.ComponentModel.Win32Exception e) { return (false, e.Message); }
    }

    private static List<string> ReadUninstallNames()
    {
        var names = new List<string>();
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is string n) names.Add(n);
            }
        }
        return names;
    }
}
