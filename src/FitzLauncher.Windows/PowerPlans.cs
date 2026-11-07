using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FitzLauncher.Windows;

public static partial class PowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    /// <summary>"Best performance" on the Windows power-mode slider (for laptops / Modern Standby PCs).</summary>
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static (Guid? guid, string name) GetActive()
    {
        if (Native.PowerGetActiveScheme(0, out var ptr) != 0) return (null, "");
        try
        {
            var guid = Marshal.PtrToStructure<Guid>(ptr);
            var name = ListSchemes().FirstOrDefault(s => s.guid == guid).name ?? "";
            return (guid, name);
        }
        finally { Native.LocalFree(ptr); }
    }

    public static bool SetActive(Guid scheme) => Native.PowerSetActiveScheme(0, in scheme) == 0;

    public static Guid? GetOverlay() => Native.PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null;
    public static bool SetOverlay(Guid overlay) => Native.PowerSetActiveOverlayScheme(in overlay) == 0;

    public static List<(Guid guid, string name)> ListSchemes()
    {
        var output = Run("/list");
        return SchemeLine().Matches(output)
            .Select(m => (Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim()))
            .ToList();
    }

    /// <summary>
    /// Finds a High Performance scheme. Many PCs hide it (Modern Standby), in which case it is created
    /// from the built-in template. Returns null if Windows refuses, e.g. on some laptops.
    /// </summary>
    public static Guid? EnsureHighPerformance()
    {
        var schemes = ListSchemes();
        if (schemes.Any(s => s.guid == HighPerformance)) return HighPerformance;
        var existing = schemes.FirstOrDefault(s => s.name.Contains("High performance", StringComparison.OrdinalIgnoreCase) ||
                                                   s.name.Contains("Fitz Gaming", StringComparison.OrdinalIgnoreCase));
        if (existing.guid != Guid.Empty) return existing.guid;

        var created = SchemeLine().Match(Run($"/duplicatescheme {HighPerformance}"));
        if (!created.Success) return null;
        var guid = Guid.Parse(created.Groups[1].Value);
        Run($"/changename {guid} \"Fitz Gaming\" \"High performance plan used by Fitz Launcher's Game Mode\"");
        return guid;
    }

    private static string Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", args)
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch (System.ComponentModel.Win32Exception) { return ""; }
    }

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\(([^)]*)\)")]
    private static partial Regex SchemeLine();
}
