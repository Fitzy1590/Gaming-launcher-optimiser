using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Windows;

/// <summary>Samples per-process CPU use and throttles background hogs to Idle priority (restored afterwards).</summary>
public sealed class BackgroundLimiter
{
    private readonly CpuHogDetector _detector = new();
    private readonly Dictionary<int, TimeSpan> _lastCpu = [];
    private readonly Dictionary<int, ProcessPriorityClass> _original = [];
    private DateTime _lastSample = DateTime.UtcNow;

    public event Action<string>? Throttled;

    public void Tick(IReadOnlyCollection<int> gamePids)
    {
        var now = DateTime.UtcNow;
        var wall = (now - _lastSample).TotalMilliseconds * Environment.ProcessorCount;
        _lastSample = now;
        var usage = new Dictionary<int, (string, double)>();
        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var cpu = p.TotalProcessorTime;
                    if (_lastCpu.TryGetValue(p.Id, out var before) && wall > 0)
                        usage[p.Id] = (p.ProcessName, (cpu - before).TotalMilliseconds / wall * 100);
                    _lastCpu[p.Id] = cpu;
                    names[p.Id] = p.ProcessName;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        foreach (var gone in _lastCpu.Keys.Where(k => !names.ContainsKey(k)).ToList()) _lastCpu.Remove(gone);

        var (throttle, release) = _detector.Update(usage, new HashSet<int>(gamePids));
        foreach (var pid in throttle)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                _original[pid] = p.PriorityClass;
                p.PriorityClass = ProcessPriorityClass.Idle;
                Throttled?.Invoke($"Throttled {p.ProcessName} (was using {usage[pid].Item2:0}% CPU)");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        foreach (var pid in release) Restore(pid);
    }

    private void Restore(int pid)
    {
        if (!_original.Remove(pid, out var priority)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.PriorityClass == ProcessPriorityClass.Idle) p.PriorityClass = priority;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void RestoreAll()
    {
        foreach (var pid in _original.Keys.ToList()) Restore(pid);
    }
}

/// <summary>Detects whether a game crashed: NTSTATUS-style exit codes, or a Windows "Application Error" event.</summary>
public static class CrashDetector
{
    /// <summary>
    /// Opens a light query handle on each game process while it's alive, so its exit code can still be
    /// read after it closes (Windows keeps the exit code while a handle is open).
    /// </summary>
    public sealed class Watch : IDisposable
    {
        private const uint QueryLimited = 0x1000, StillActive = 259;
        private readonly Dictionary<int, Microsoft.Win32.SafeHandles.SafeProcessHandle> _handles = [];

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle h, out uint code);

        public void Track(IEnumerable<int> pids)
        {
            foreach (var pid in pids.Where(p => !_handles.ContainsKey(p)))
            {
                var h = OpenProcess(QueryLimited, false, pid);
                if (h.IsInvalid) { h.Dispose(); continue; }
                _handles[pid] = h;
            }
        }

        /// <summary>The first crash exit code among the game's processes (NTSTATUS errors are 0xC0000000+).</summary>
        public uint? CrashExitCode()
        {
            foreach (var h in _handles.Values)
                if (GetExitCodeProcess(h, out var code) && code != StillActive && code >= 0xC0000000) return code;
            return null;
        }

        public void Dispose()
        {
            foreach (var h in _handles.Values) h.Dispose();
        }
    }

    /// <summary>Faulting module from the Application log's "Application Error" (1000) entry for this exe.</summary>
    public static string? FaultingModule(string exeName, DateTimeOffset since)
    {
        try
        {
            var ms = (long)Math.Max(1000, (DateTimeOffset.Now - since).TotalMilliseconds + 60_000);
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[timediff(@SystemTime) <= {ms}]]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
            {
                using (e)
                {
                    var props = e.Properties;
                    if (props.Count > 3 && string.Equals(props[0].Value?.ToString(), exeName, StringComparison.OrdinalIgnoreCase))
                        return props[3].Value?.ToString();
                }
            }
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException or InvalidOperationException) { }
        return null;
    }
}

/// <summary>Time since the last keyboard/mouse input anywhere on the system.</summary>
public static class InputIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    public static TimeSpan SinceLastKeyboardOrMouse()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }
}
