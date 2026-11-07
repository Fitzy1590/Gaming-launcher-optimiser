using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FitzLauncher.Windows;

internal static partial class Native
{
    // ------------------------------------------------------------ memory

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(nint hProcess);

    internal const int SystemMemoryListInformationClass = 80;
    internal const int MemoryPurgeStandbyList = 4;

    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetSystemInformation(int infoClass, ref int info, int length);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(int infoClass, ref SystemMemoryListInfo info, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemMemoryListInfo
    {
        public nuint ZeroPageCount;
        public nuint FreePageCount;
        public nuint ModifiedPageCount;
        public nuint ModifiedNoWritePageCount;
        public nuint BadPageCount;
        public PageCounts8 PageCountByPriority;      // standby list, per priority
        public PageCounts8 RepurposedPagesByPriority;
        public nuint ModifiedPageCountPageFile;
    }

    [InlineArray(8)]
    internal struct PageCounts8 { private nuint _e0; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    // ------------------------------------------------------------ privileges

    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    internal const uint TOKEN_QUERY = 0x8;
    internal const uint SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, int len, nint prev, nint retLen);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    internal static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    // ------------------------------------------------------------ processes

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint PROCESS_SUSPEND_RESUME = 0x0800;
    internal const uint PROCESS_SET_QUOTA = 0x0100;
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_CREATE_PROCESS = 0x0080;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(nint process, int flags, [Out] char[] name, ref int size);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtSuspendProcess(nint process);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtResumeProcess(nint process);

    /// <summary>Full image path of a process, using the cheap limited-information access right.</summary>
    internal static string? GetProcessPath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(h, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    // ------------------------------------------------------------ timer

    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool set, out uint current);

    // ------------------------------------------------------------ power

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetActiveScheme(nint rootKey, out nint activeScheme);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerSetActiveScheme(nint rootKey, in Guid scheme);

    /// <summary>Windows 10 1709+ power-mode slider ("Best performance"). Undocumented but stable.</summary>
    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerSetActiveOverlayScheme(in Guid overlay);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint mem);

    // ------------------------------------------------------------ displays

    internal const int ENUM_CURRENT_SETTINGS = -1;
    internal const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    internal const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice dd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);

    /// <summary>Plays a system sound (0x40 = asterisk, 0x30 = exclamation). Audible in exclusive fullscreen.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MessageBeep(uint type);

    // ------------------------------------------------------------ un-elevated launch

    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, out int pid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(nint token, uint access, nint attrs, int impersonationLevel, int tokenType, out nint newToken);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessWithTokenW(nint token, int logonFlags, string? app, string? cmdLine,
        int creationFlags, nint env, string? currentDir, ref StartupInfo si, out ProcessInformation pi);
}
