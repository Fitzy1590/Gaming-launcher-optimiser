using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FitzLauncher.Windows;

public static class GameLauncher
{
    /// <summary>
    /// Starts the game. When the launcher is elevated, the game is started with the desktop shell's
    /// (non-admin) token, as if double-clicked: elevated games can't talk to a non-elevated Steam,
    /// and some anti-cheat/DRM refuses to run elevated. Falls back to a normal start.
    /// </summary>
    public static int Start(string exePath, string arguments, bool asAdmin)
    {
        var workingDir = Path.GetDirectoryName(exePath)!;
        if (!asAdmin && Environment.IsPrivilegedProcess && TryStartUnelevated(exePath, arguments, workingDir, out var pid))
            return pid;

        var psi = new ProcessStartInfo(exePath, arguments) { WorkingDirectory = workingDir, UseShellExecute = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("The game did not start.");
        return p.Id;
    }

    private static bool TryStartUnelevated(string exe, string args, string dir, out int pid)
    {
        pid = 0;
        var shell = Native.GetShellWindow();
        if (shell == 0) return false;
        Native.GetWindowThreadProcessId(shell, out var shellPid);
        var hProc = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION, false, shellPid);
        if (hProc == 0) return false;
        nint shellToken = 0, primary = 0;
        try
        {
            const uint TOKEN_DUPLICATE = 0x2;
            if (!Native.OpenProcessToken(hProc, TOKEN_DUPLICATE, out shellToken)) return false;
            const uint access = 0x0001 | 0x0002 | 0x0008 | 0x0080 | 0x0100; // assign primary, duplicate, query, adjust default, adjust session
            if (!Native.DuplicateTokenEx(shellToken, access, 0, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out primary)) return false;
            var si = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>() };
            var cmd = $"\"{exe}\" {args}".Trim();
            if (!Native.CreateProcessWithTokenW(primary, 0, exe, cmd, 0, 0, dir, ref si, out var pi)) return false;
            pid = pi.dwProcessId;
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (primary != 0) Native.CloseHandle(primary);
            if (shellToken != 0) Native.CloseHandle(shellToken);
            Native.CloseHandle(hProc);
        }
    }
}

/// <summary>
/// Tracks every process belonging to a game: the one we started, plus anything running from the
/// install folder (launchers often start the real exe and exit; Unreal games hand off to
/// *-Win64-Shipping.exe). Cheap: uses QueryFullProcessImageName and caches results per PID.
/// </summary>
public sealed class GameProcessTracker(int rootPid, string installDir)
{
    private readonly Dictionary<int, bool> _isGame = [];
    private readonly HashSet<int> _alive = [];
    private readonly Lock _lock = new();
    private readonly string _root = installDir.TrimEnd('\\') + "\\";

    public IReadOnlyCollection<int> Pids { get { lock (_lock) return _alive.ToArray(); } }

    public bool Contains(int pid) { lock (_lock) return _alive.Contains(pid); }

    /// <summary>Re-checks running processes. Returns the number of game processes alive.</summary>
    public int Refresh()
    {
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var pid = p.Id;
                seen.Add(pid);
                if (!_isGame.TryGetValue(pid, out var isGame))
                {
                    var path = Native.GetProcessPath(pid);
                    isGame = pid == rootPid || (path is not null && path.StartsWith(_root, StringComparison.OrdinalIgnoreCase));
                    _isGame[pid] = isGame;
                }
            }
        }
        foreach (var dead in _isGame.Keys.Where(k => !seen.Contains(k)).ToList()) _isGame.Remove(dead);
        lock (_lock)
        {
            _alive.Clear();
            foreach (var (pid, g) in _isGame) if (g) _alive.Add(pid);
            return _alive.Count;
        }
    }
}
