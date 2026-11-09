using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FitzLauncher.Windows;

/// <summary>Finding process windows and bringing them to the front, even while a game holds the foreground.</summary>
public static class ForegroundWindow
{
    /// <summary>The first visible top-level window owned by any of these processes (0 if none).</summary>
    public static nint Find(IEnumerable<int> pids)
    {
        var set = pids.ToHashSet();
        nint found = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!set.Contains((int)pid)) return true;
            found = h;
            return false;
        }, 0);
        return found;
    }

    public static bool IsForeground(IEnumerable<int> pids)
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pids.Contains((int)pid);
    }

    /// <summary>Waits for a process's main window (null after the timeout or if it exits).</summary>
    public static async Task<nint?> WaitForWindowAsync(Process p, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            try
            {
                if (p.HasExited) return null;
                p.Refresh();
                if (p.MainWindowHandle != 0) return p.MainWindowHandle;
                if (Find([p.Id]) is var w and not 0) return w;
            }
            catch (InvalidOperationException) { return null; }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Brings a window forward even while a game has the foreground: joins the foreground thread's input queue
    /// for the call (SetForegroundWindow alone is refused for background processes).
    /// </summary>
    public static void Activate(nint hwnd)
    {
        if (hwnd == 0) return;
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
    }

    private delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
