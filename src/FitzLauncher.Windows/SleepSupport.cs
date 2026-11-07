using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

public enum PowerEvent { DisplayOff, DisplayOn, Suspend, Resume }

/// <summary>
/// Screen on/off and sleep/resume notifications without a window, via callbacks. Screen off covers the
/// power button and idle timeout on handhelds (Modern Standby); suspend/resume covers classic PC sleep (S3).
/// </summary>
public sealed class PowerEvents : IDisposable
{
    private delegate uint Callback(nint context, uint type, nint setting);

    [DllImport("powrprof.dll")] private static extern uint PowerSettingRegisterNotification(in Guid setting, uint flags, nint recipient, out nint handle);
    [DllImport("powrprof.dll")] private static extern uint PowerSettingUnregisterNotification(nint handle);
    [DllImport("powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, nint recipient, out nint handle);
    [DllImport("powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(nint handle);

    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmSuspend = 0x4, PbtApmResumeSuspend = 0x7, PbtApmResumeAutomatic = 0x12, PbtPowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly Callback _callback; // kept alive while registered
    private readonly nint _params;
    private nint _display, _suspend;

    public event Action<PowerEvent>? Changed;

    public PowerEvents()
    {
        _callback = OnEvent;
        _params = Marshal.AllocHGlobal(2 * IntPtr.Size);
        Marshal.WriteIntPtr(_params, 0, Marshal.GetFunctionPointerForDelegate(_callback));
        Marshal.WriteIntPtr(_params, IntPtr.Size, 0);
        try
        {
            if (PowerSettingRegisterNotification(in ConsoleDisplayState, DeviceNotifyCallback, _params, out _display) != 0) _display = 0;
            if (PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _params, out _suspend) != 0) _suspend = 0;
        }
        catch (EntryPointNotFoundException) { }
    }

    public bool Working => _display != 0 || _suspend != 0;

    private uint OnEvent(nint context, uint type, nint setting)
    {
        try
        {
            PowerEvent? e = type switch
            {
                PbtApmSuspend => PowerEvent.Suspend,
                PbtApmResumeSuspend or PbtApmResumeAutomatic => PowerEvent.Resume,
                PbtPowerSettingChange when setting != 0 && Marshal.PtrToStructure<Guid>(setting) == ConsoleDisplayState =>
                    Marshal.ReadInt32(setting, 20) switch { 0 => PowerEvent.DisplayOff, 1 => PowerEvent.DisplayOn, _ => null }, // 2 = dimmed
                _ => null,
            };
            if (e is { } ev) Changed?.Invoke(ev);
        }
        catch (Exception) { /* never throw into the power manager */ }
        return 0;
    }

    public void Dispose()
    {
        if (_display != 0) PowerSettingUnregisterNotification(_display);
        if (_suspend != 0) PowerUnregisterSuspendResumeNotification(_suspend);
        _display = _suspend = 0;
        Marshal.FreeHGlobal(_params);
    }
}

/// <summary>
/// Freezes and unfreezes the game's processes. The frozen PIDs are written to disk first, so if the
/// launcher itself is closed or crashes, the game is unfrozen on exit or at the next start.
/// </summary>
public sealed class GameFreezer(AppPaths paths)
{
    private readonly Lock _lock = new();
    private readonly List<int> _frozen = [];
    private string File_ => Path.Combine(paths.Root, "frozen-game.json");

    public int Freeze(IEnumerable<int> pids)
    {
        lock (_lock)
        {
            foreach (var pid in pids.Distinct().Where(p => !_frozen.Contains(p)))
            {
                var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
                if (h == 0) continue; // protected (anti-cheat) or gone
                if (Native.NtSuspendProcess(h) == 0) _frozen.Add(pid);
                Native.CloseHandle(h);
            }
            Json.Save(File_, _frozen);
            return _frozen.Count;
        }
    }

    public void Unfreeze()
    {
        lock (_lock)
        {
            Resume(_frozen);
            _frozen.Clear();
            try { File.Delete(File_); } catch (IOException) { }
        }
    }

    /// <summary>Unfreezes anything a previous launcher run left frozen.</summary>
    public static bool RecoverIfNeeded(AppPaths paths)
    {
        var file = Path.Combine(paths.Root, "frozen-game.json");
        if (Json.Load<List<int>>(file) is not { Count: > 0 } pids) return false;
        Resume(pids);
        try { File.Delete(file); } catch (IOException) { }
        return true;
    }

    private static void Resume(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
            if (h == 0) continue;
            Native.NtResumeProcess(h);
            Native.CloseHandle(h);
        }
    }
}

/// <summary>
/// Lets Windows' normal "turn off screen / sleep after" timers work during a game. Many games hold a
/// "keep the display on" request for their whole run; this overrides it for the game's exe while it
/// plays (powercfg /requestsoverride, removed afterwards and after a crash). Controller input then
/// counts as activity through <see cref="KeepAwake.Ping"/>, since Windows ignores gamepads.
/// </summary>
public static class PowerRequestOverride
{
    private static string FileFor(AppPaths paths) => Path.Combine(paths.Root, "power-request-overrides.json");

    public static bool Set(AppPaths paths, IEnumerable<string> exeNames)
    {
        var names = exeNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Json.Save(FileFor(paths), names);
        var ok = true;
        foreach (var n in names) ok &= Powercfg($"/requestsoverride PROCESS \"{n}\" DISPLAY SYSTEM AWAYMODE") == 0;
        return ok;
    }

    public static void Clear(AppPaths paths)
    {
        if (Json.Load<List<string>>(FileFor(paths)) is { } names)
            foreach (var n in names) Powercfg($"/requestsoverride PROCESS \"{n}\"");
        try { File.Delete(FileFor(paths)); } catch (IOException) { }
    }

    private static int Powercfg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return -1; }
    }
}

public static class KeepAwake
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private const uint SystemRequired = 0x1, DisplayRequired = 0x2;

    /// <summary>Resets Windows' idle timers once (as if the user had touched the mouse).</summary>
    public static void Ping() => SetThreadExecutionState(SystemRequired | DisplayRequired);
}

/// <summary>Desktop helpers for resuming: is the user signed back in, and putting the game back in front.</summary>
public static class DesktopState
{
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint obj, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);

    /// <summary>False while the lock / sign-in screen is showing.</summary>
    public static bool IsUnlocked()
    {
        var h = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (h == 0) return false;
        try
        {
            var name = new StringBuilder(64);
            return GetUserObjectInformation(h, 2 /* UOI_NAME */, name, name.Capacity * 2, out _) &&
                   name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(h); }
    }

    /// <summary>The foreground window, if it belongs to one of these processes.</summary>
    public static nint ForegroundOf(IReadOnlyCollection<int> pids)
    {
        var hwnd = GetForegroundWindow();
        return hwnd != 0 && GetWindowThreadProcessId(hwnd, out var pid) != 0 && pids.Contains(pid) ? hwnd : 0;
    }

    public static void BringBack(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || GetForegroundWindow() == hwnd) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
    }
}
