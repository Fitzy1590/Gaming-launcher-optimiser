using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FitzLauncher.Windows.Optimization;

/// <summary>
/// Gives a game's disk reads top priority over background indexing and downloads. Per-process, so it ends with the
/// game and needs no restore. High needs admin; without it the call fails and nothing changes.
/// </summary>
public static partial class ProcessIoPriority
{
    // ProcessIoPriority = 33 (ntdll/winternl community headers, not in Microsoft docs; VERIFY by reading back).
    private const int ProcessIoPriorityClass = 33;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint IoPriorityNormal = 2, IoPriorityHigh = 3;

    [LibraryImport("ntdll.dll")]
    private static partial int NtSetInformationProcess(nint process, int infoClass, ref uint info, int length);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, ref uint info, int length, out int returned);

    /// <summary>Sets I/O priority High on the given processes. Returns plain-English lines for those that changed.</summary>
    public static List<string> RaiseToHigh(IEnumerable<int> pids)
    {
        var report = new List<string>();
        Native.EnablePrivilege("SeIncreaseBasePriorityPrivilege");
        foreach (var pid in pids)
        {
            var h = Native.OpenProcess(PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == 0) continue;
            try
            {
                uint current = IoPriorityNormal;
                if (NtQueryInformationProcess(h, ProcessIoPriorityClass, ref current, sizeof(uint), out _) == 0 && current >= IoPriorityHigh) continue;
                var hint = IoPriorityHigh;
                if (NtSetInformationProcess(h, ProcessIoPriorityClass, ref hint, sizeof(uint)) == 0)
                    report.Add($"{ProcessName(pid)}: disk priority High (ends when the game closes)");
            }
            finally { Native.CloseHandle(h); }
        }
        return report;
    }

    private static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return $"PID {pid}"; }
    }
}
