using System.Runtime.InteropServices;
using FitzLauncher.Core.Diagnostics;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows.Diagnostics;

/// <summary>Gathers the facts for the "Export diagnostic package" button (SPEC §17). Reads only; nothing is changed.</summary>
public static partial class DiagnosticSnapshot
{
    // NtQueryTimerResolution (ntdll, undocumented in MS Learn; values in 100 ns units). VERIFY on device.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    public static string Export(AppPaths paths, string version)
    {
        var sections = new List<(string, IEnumerable<string>)>
        {
            ("system", new[]
            {
                $"Fitz Launcher {version}",
                $"OS: {RuntimeInformation.OSDescription}",
                $"Runtime: {RuntimeInformation.FrameworkDescription}",
                $"Logical CPUs: {Environment.ProcessorCount}",
                RamLine(),
                $"Power plan: {PowerPlans.GetActive().name}",
                TimerLine(),
                $"Fitz process affinity mask: 0x{(long)System.Diagnostics.Process.GetCurrentProcess().ProcessorAffinity:X}",
            }),
            ("restore", new[]
            {
                $"gamemode-restore.json present: {File.Exists(paths.GameModeState)}",
                $"eqapo-restore.json present: {File.Exists(paths.EqualizerApoState)}",
            }),
            ("log", TailLog(paths.LogFile, 400)),
        };
        var text = DiagnosticPackage.Build(DateTimeOffset.Now, sections);
        var dir = Path.Combine(paths.Root, "diagnostics");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"fitz-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(file, text);
        return file;
    }

    private static string RamLine()
    {
        var m = MemoryCleaner.Snapshot();
        return $"RAM: {m.TotalMb / 1024.0:0.0} GB total, {m.AvailableMb / 1024.0:0.0} GB available";
    }

    private static string TimerLine() =>
        NtQueryTimerResolution(out var min, out var max, out var cur) == 0
            ? $"Timer resolution now: {cur / 10000.0:0.000} ms (finest allowed {max / 10000.0:0.000} ms, coarsest {min / 10000.0:0.000} ms)"
            : "Timer resolution: unavailable";

    private static IEnumerable<string> TailLog(string path, int lines)
    {
        try { return File.Exists(path) ? File.ReadLines(path).TakeLast(lines).ToList() : ["(no launcher.log yet)"]; }
        catch (IOException e) { return ["(log unreadable: " + e.Message + ")"]; }
    }
}
