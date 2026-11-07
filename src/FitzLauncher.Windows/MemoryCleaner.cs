using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FitzLauncher.Windows;

public readonly record struct MemorySnapshot(long TotalMb, long AvailableMb, long FreeMb, long StandbyMb);

/// <summary>
/// RAM clearing. "Standby" memory is file cache Windows will hand back when asked, but some games
/// stutter while Windows repurposes it under pressure, so purging it before and during play (as
/// ISLC does) can smooth 1% lows on 8-16 GB systems. Needs admin.
/// </summary>
public static class MemoryCleaner
{
    private static bool _privilegesEnabled;

    private static void EnsurePrivileges()
    {
        if (_privilegesEnabled) return;
        Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
        Native.EnablePrivilege("SeIncreaseQuotaPrivilege");
        Native.EnablePrivilege("SeDebugPrivilege");
        _privilegesEnabled = true;
    }

    public static MemorySnapshot Snapshot()
    {
        var status = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        Native.GlobalMemoryStatusEx(ref status);
        long freeMb = (long)(status.AvailPhys / 1024 / 1024), standbyMb = 0;

        EnsurePrivileges();
        var info = new Native.SystemMemoryListInfo();
        if (Native.NtQuerySystemInformation(Native.SystemMemoryListInformationClass, ref info, Marshal.SizeOf<Native.SystemMemoryListInfo>(), out _) == 0)
        {
            const long page = 4096;
            freeMb = (long)(info.ZeroPageCount + info.FreePageCount) * page / 1024 / 1024;
            nuint standbyPages = 0;
            for (var i = 0; i < 8; i++) standbyPages += info.PageCountByPriority[i];
            standbyMb = (long)standbyPages * page / 1024 / 1024;
        }
        return new MemorySnapshot((long)(status.TotalPhys / 1024 / 1024), (long)(status.AvailPhys / 1024 / 1024), freeMb, standbyMb);
    }

    /// <summary>Purges the standby list. Returns MB released (approximate).</summary>
    public static long PurgeStandbyList()
    {
        EnsurePrivileges();
        var before = Snapshot();
        var command = Native.MemoryPurgeStandbyList;
        var status = Native.NtSetSystemInformation(Native.SystemMemoryListInformationClass, ref command, sizeof(int));
        if (status != 0) return 0;
        var after = Snapshot();
        return Math.Max(0, after.FreeMb - before.FreeMb);
    }

    /// <summary>
    /// Trims working sets of background processes (not the game, not system-critical ones), moving
    /// idle pages out of RAM. Only used on low-RAM machines: it makes those apps page back in later.
    /// </summary>
    public static int TrimBackgroundWorkingSets(IReadOnlySet<int> excludePids)
    {
        EnsurePrivileges();
        var trimmed = 0;
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4 || excludePids.Contains(p.Id) || p.SessionId == 0 && p.Id != self) continue;
                var h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SET_QUOTA, false, p.Id);
                if (h == 0) continue;
                if (Native.EmptyWorkingSet(h)) trimmed++;
                Native.CloseHandle(h);
            }
        }
        return trimmed;
    }

    /// <summary>Releases the launcher's own memory while a game runs.</summary>
    public static void TrimSelf()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Native.EmptyWorkingSet(Native.GetCurrentProcess());
    }
}
