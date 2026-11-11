using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Optimization;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

/// <summary>
/// Per-process GPU use and video memory from the Windows graphics counters ("GPU Engine" and
/// "GPU Process Memory"), the same figures Task Manager shows and the source shanselman/gputaskman
/// reads on Windows. They cover every graphics API because they come from the graphics kernel.
/// </summary>
public sealed partial class GpuProcessCounters
{
    [GeneratedRegex(@"pid_(\d+)_.*engtype_(.+)$")]
    private static partial Regex EngineName();

    [GeneratedRegex(@"pid_(\d+)_")]
    private static partial Regex PidName();

    private Dictionary<string, CounterSample> _lastEngine = [];
    private bool _failed;

    /// <summary>False once the counters turned out to be missing (old Windows, broken counter registry).</summary>
    public bool Available => !_failed;

    /// <summary>GPU % (busiest engine type, as Task Manager shows it) and video memory in MB per process id.
    /// GPU % needs two calls a moment apart; the first call returns memory only.</summary>
    public Dictionary<int, (double Gpu, long VramMb)> Read()
    {
        var result = new Dictionary<int, (double Gpu, long VramMb)>();
        if (_failed) return result;
        try
        {
            var engines = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            var current = new Dictionary<string, CounterSample>();
            var perType = new Dictionary<(int Pid, string Type), double>();
            if (engines["Utilization Percentage"] is { } util)
            {
                foreach (InstanceData d in util.Values)
                {
                    current[d.InstanceName] = d.Sample;
                    if (!_lastEngine.TryGetValue(d.InstanceName, out var before) || EngineName().Match(d.InstanceName) is not { Success: true } m) continue;
                    var pct = CounterSample.Calculate(before, d.Sample);
                    if (float.IsNaN(pct) || pct <= 0) continue;
                    var key = (int.Parse(m.Groups[1].Value), m.Groups[2].Value);
                    perType[key] = perType.GetValueOrDefault(key) + pct;
                }
            }
            _lastEngine = current;
            foreach (var g in perType.GroupBy(kv => kv.Key.Pid))
                result[g.Key] = (Math.Min(100, g.Max(kv => kv.Value)), 0);

            var memory = new PerformanceCounterCategory("GPU Process Memory").ReadCategory();
            foreach (var counter in new[] { "Dedicated Usage", "Shared Usage" })
            {
                if (memory[counter] is not { } col) continue;
                foreach (InstanceData d in col.Values)
                {
                    if (PidName().Match(d.InstanceName) is not { Success: true } m) continue;
                    var pid = int.Parse(m.Groups[1].Value);
                    var have = result.GetValueOrDefault(pid);
                    result[pid] = (have.Gpu, have.VramMb + d.RawValue / (1024 * 1024));
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException or FormatException or OverflowException)
        {
            _failed = true; // CPU and memory still work without GPU figures
        }
        return result;
    }
}

/// <summary>Measures what every app in your session is using: CPU, memory, GPU and video memory.</summary>
public sealed class ProcessResourceSampler
{
    private readonly GpuProcessCounters _gpu = new();
    private readonly Dictionary<int, TimeSpan> _lastCpu = [];
    private DateTime _lastSample = DateTime.MinValue;

    public bool GpuAvailable => _gpu.Available;

    /// <summary>Usage since the previous call. The first call has no CPU or GPU percentages yet.</summary>
    /// <param name="findWindows">Also note which apps have a visible window (needed before offering to close one).</param>
    public List<ProcessUsage> Sample(bool findWindows = false)
    {
        var now = DateTime.UtcNow;
        var wall = _lastSample == DateTime.MinValue ? 0 : (now - _lastSample).TotalMilliseconds * Environment.ProcessorCount;
        _lastSample = now;
        var gpu = _gpu.Read();
        var windows = findWindows ? WindowOwners() : [];
        var list = new List<ProcessUsage>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    // Services and Windows itself (session 0) are left to Game Mode's own service list.
                    if (p.Id <= 4 || p.SessionId == 0) continue;
                    var cpu = p.TotalProcessorTime;
                    var pct = _lastCpu.TryGetValue(p.Id, out var before) && wall > 0 ? Math.Max(0, (cpu - before).TotalMilliseconds / wall * 100) : 0;
                    _lastCpu[p.Id] = cpu;
                    seen.Add(p.Id);
                    var g = gpu.GetValueOrDefault(p.Id);
                    list.Add(new ProcessUsage(p.Id, p.ProcessName, pct, p.WorkingSet64 / (1024 * 1024), g.Gpu, g.VramMb, windows.Contains(p.Id)));
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
        }
        foreach (var gone in _lastCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _lastCpu.Remove(gone);
        return list;
    }

    /// <summary>Two readings a second apart, so the percentages are real. For use before a game starts.</summary>
    public async Task<List<ProcessUsage>> MeasureAsync(CancellationToken ct)
    {
        Sample();
        await Task.Delay(1000, ct).ConfigureAwait(false);
        return Sample(findWindows: true);
    }

    // EnumWindows, IsWindowVisible, GetWindowThreadProcessId, GetWindowTextLengthW: winuser.h
    // (learn.microsoft.com/windows/win32/api/winuser). One pass over top-level windows.
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint hwnd);

    private static HashSet<int> WindowOwners()
    {
        var owners = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowTextLengthW(hwnd) > 0 && GetWindowThreadProcessId(hwnd, out var pid) != 0) owners.Add((int)pid);
            return true;
        }, 0);
        return owners;
    }
}

/// <summary>What the resource guard changed, saved before each change so it can be undone even after a crash.</summary>
public sealed class ResourceGuardRestoreState
{
    public sealed class Entry
    {
        public int Pid { get; set; }
        /// <summary>Checked before undoing, so a reused process id is never touched.</summary>
        public string Name { get; set; } = "";
        public ProcessPriorityClass? Priority { get; set; }
        public bool Efficiency { get; set; }
        /// <summary>GPU scheduling class before it was lowered (2 = normal).</summary>
        public int? GpuPriority { get; set; }
        public bool Suspended { get; set; }
    }

    public List<Entry> Entries { get; set; } = [];
}

/// <summary>
/// Applies the guard's decisions to background apps and puts everything back afterwards. Nothing here
/// touches the game. Apps are never force-closed: Close only asks, the way clicking the X does.
/// </summary>
public sealed class ResourceApplier(AppPaths paths, Action<string>? log = null)
{
    private readonly ResourceGuardRestoreState _state = new();
    private readonly Lock _lock = new();
    private readonly List<string> _closed = [];

    /// <summary>Apps asked to close this session (they stay closed; shown at the end).</summary>
    public IReadOnlyList<string> Closed => _closed;

    // SetProcessInformation(ProcessPowerThrottling) = Windows "efficiency mode" (EcoQoS): processthreadsapi.h,
    // PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask, StateMask }, EXECUTION_SPEED = 0x1.
    // ControlMask = 0 hands the decision back to Windows.
    // learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingExecutionSpeed = 0x1;
    private const uint ProcessSetInformation = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(nint process, int infoClass, ref PowerThrottlingState info, uint size);

    // GPU scheduling priority of a process: d3dkmthk.h, D3DKMT_SCHEDULINGPRIORITYCLASS
    // (IDLE = 0, BELOW_NORMAL = 1, NORMAL = 2). Both return NTSTATUS (0 = success).
    // learn.microsoft.com/windows-hardware/drivers/ddi/d3dkmthk/nf-d3dkmthk-d3dkmtsetprocessschedulingpriorityclass
    private const int GpuPriorityIdle = 0, GpuPriorityNormal = 2;

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priority);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTGetProcessSchedulingPriorityClass(nint process, out int priority);

    /// <returns>How many processes of the app were changed.</returns>
    public int Apply(AppUsage app, FreeAction action)
    {
        var done = 0;
        foreach (var pid in app.Pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                // The reading is a few seconds old: make sure this is still the same app, and still allowed.
                if (!string.Equals(p.ProcessName, app.Name, StringComparison.OrdinalIgnoreCase) || !AppClassifier.MayTouch(p.ProcessName)) continue;
                if (action switch
                {
                    FreeAction.Restrain => Restrain(p),
                    FreeAction.TrimMemory => Trim(pid),
                    FreeAction.Pause => Pause(p),
                    _ => p.MainWindowHandle != 0 && p.CloseMainWindow(),
                }) done++;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { }
        }
        if (done > 0 && action == FreeAction.Close) lock (_lock) _closed.Add(app.Name);
        return done;
    }

    private bool Restrain(Process p)
    {
        ResourceGuardRestoreState.Entry entry;
        lock (_lock)
        {
            if (_state.Entries.Any(e => e.Pid == p.Id && (e.Priority is not null || e.Efficiency))) return false; // already held back
            entry = new ResourceGuardRestoreState.Entry { Pid = p.Id, Name = p.ProcessName, Priority = p.PriorityClass, Efficiency = true };
            _state.Entries.Add(entry);
            Persist(); // record first, so a crash mid-change still restores
        }
        if (entry.Priority is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal or ProcessPriorityClass.BelowNormal) p.PriorityClass = ProcessPriorityClass.Idle;
        var h = Native.OpenProcess(ProcessSetInformation | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
        if (h == 0) return true;
        try
        {
            var on = new PowerThrottlingState { Version = 1, ControlMask = PowerThrottlingExecutionSpeed, StateMask = PowerThrottlingExecutionSpeed };
            SetProcessInformation(h, ProcessPowerThrottling, ref on, (uint)Marshal.SizeOf<PowerThrottlingState>());
            try
            {
                var was = D3DKMTGetProcessSchedulingPriorityClass(h, out var current) == 0 ? current : GpuPriorityNormal;
                if (was > GpuPriorityIdle && was <= GpuPriorityNormal)
                {
                    lock (_lock) { entry.GpuPriority = was; Persist(); }
                    D3DKMTSetProcessSchedulingPriorityClass(h, GpuPriorityIdle);
                }
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }
        }
        finally { Native.CloseHandle(h); }
        return true;
    }

    private static bool Trim(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SET_QUOTA, false, pid);
        if (h == 0) return false;
        try { return Native.EmptyWorkingSet(h); }
        finally { Native.CloseHandle(h); }
    }

    private bool Pause(Process p)
    {
        lock (_lock)
        {
            if (_state.Entries.Any(e => e.Pid == p.Id && e.Suspended)) return false;
            _state.Entries.Add(new ResourceGuardRestoreState.Entry { Pid = p.Id, Name = p.ProcessName, Suspended = true });
            Persist();
        }
        var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, p.Id);
        if (h == 0) return false;
        try { return Native.NtSuspendProcess(h) == 0; }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Puts one app back the way it was (used when holding it back made the game run worse).</summary>
    public void Undo(string name)
    {
        List<ResourceGuardRestoreState.Entry> entries;
        lock (_lock)
        {
            entries = [.. _state.Entries.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))];
            _state.Entries.RemoveAll(entries.Contains);
        }
        foreach (var e in entries) RestoreEntry(e);
        lock (_lock) Persist();
    }

    public List<string> RestoreAll()
    {
        List<ResourceGuardRestoreState.Entry> entries;
        lock (_lock) { entries = [.. _state.Entries]; _state.Entries.Clear(); }
        var report = Restore(entries);
        try { File.Delete(paths.ResourceGuardState); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (_closed.Count > 0) report.Add($"Closed for this session (reopen them yourself if you need them): {string.Join(", ", _closed.Distinct(StringComparer.OrdinalIgnoreCase))}");
        return report;
    }

    /// <summary>Call at launcher start: undoes changes left behind by a crash or power loss.</summary>
    public static List<string> RecoverIfNeeded(AppPaths paths)
    {
        if (!File.Exists(paths.ResourceGuardState)) return [];
        var state = Json.Load<ResourceGuardRestoreState>(paths.ResourceGuardState);
        var report = state is null ? [] : Restore(state.Entries);
        try { File.Delete(paths.ResourceGuardState); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return report;
    }

    private static List<string> Restore(List<ResourceGuardRestoreState.Entry> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) if (RestoreEntry(e)) names.Add(e.Name);
        return names.Count == 0 ? [] : [$"Background apps back to normal: {string.Join(", ", names)}"];
    }

    private static bool RestoreEntry(ResourceGuardRestoreState.Entry e)
    {
        try
        {
            using var p = Process.GetProcessById(e.Pid);
            if (!string.Equals(p.ProcessName, e.Name, StringComparison.OrdinalIgnoreCase)) return false; // the id now belongs to something else
            if (e.Suspended)
            {
                var hs = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, e.Pid);
                if (hs != 0) { Native.NtResumeProcess(hs); Native.CloseHandle(hs); }
            }
            if (e.Priority is { } priority && p.PriorityClass == ProcessPriorityClass.Idle && priority != ProcessPriorityClass.Idle) p.PriorityClass = priority;
            if (e.Efficiency || e.GpuPriority is not null)
            {
                var h = Native.OpenProcess(ProcessSetInformation, false, e.Pid);
                if (h != 0)
                {
                    try
                    {
                        if (e.Efficiency)
                        {
                            var off = new PowerThrottlingState { Version = 1, ControlMask = 0, StateMask = 0 };
                            SetProcessInformation(h, ProcessPowerThrottling, ref off, (uint)Marshal.SizeOf<PowerThrottlingState>());
                        }
                        if (e.GpuPriority is { } gp) D3DKMTSetProcessSchedulingPriorityClass(h, gp);
                    }
                    catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
                    finally { Native.CloseHandle(h); }
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return false; }
    }

    private void Persist()
    {
        try { Json.Save(paths.ResourceGuardState, _state); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Invoke("Could not save the resource guard's restore file: " + e.Message); }
    }
}
