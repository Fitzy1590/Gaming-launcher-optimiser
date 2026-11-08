using System.Diagnostics;
using System.Runtime.InteropServices;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

/// <summary>
/// Makes "sleep" on a handheld actually quiet. Modern Standby only turns the screen off; the chip keeps its power
/// limit and anything still polling (sensors, controllers, the game) keeps it warm, so the fans run. When the screen
/// goes off this switches Windows to its best power efficiency mode, drops the APU power limit to a few watts
/// (RyzenAdj, when available), and tells the launcher to stop its own polling. Everything is put back on wake.
/// Optionally sets Windows' "hibernate after" so a long sleep becomes a real power-off.
/// </summary>
public sealed class LowPowerSleep : IDisposable
{
    private static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(in Guid scheme);

    private sealed record Saved(Guid? Overlay, bool PowerLowered);

    private readonly AppPaths _paths;
    private readonly Func<RyzenAdjClient?> _ryzenAdj;
    private readonly Func<int> _sleepWatts;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _sessionOwnsPower;
    private readonly Action<string> _log;
    private readonly PowerEvents _events = new();
    private readonly Lock _lock = new();
    private bool _asleep;

    /// <summary>True from screen-off/sleep until wake: background work should pause.</summary>
    public static bool Asleep { get; private set; }

    /// <summary>Raised (on a worker thread) when the device goes to sleep and when it wakes.</summary>
    public event Action<bool>? AsleepChanged;

    private string RestoreFile => Path.Combine(_paths.Root, "sleep-restore.json");

    public LowPowerSleep(AppPaths paths, Func<RyzenAdjClient?> ryzenAdj, Func<int> sleepWatts, Func<bool> enabled, Func<bool> sessionOwnsPower, Action<string> log)
    {
        (_paths, _ryzenAdj, _sleepWatts, _enabled, _sessionOwnsPower, _log) = (paths, ryzenAdj, sleepWatts, enabled, sessionOwnsPower, log);
        _events.Changed += OnPower;
    }

    private void OnPower(PowerEvent e)
    {
        if (e is PowerEvent.DisplayOff or PowerEvent.Suspend) Enter();
        else Leave(); // DisplayOn / Resume
    }

    private void Enter()
    {
        lock (_lock)
        {
            if (_asleep) return;
            _asleep = Asleep = true;
        }
        AsleepChanged?.Invoke(true);
        if (!_enabled()) return;

        Guid? overlay = null;
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var current) == 0 && current != BestEfficiency && PowerSetActiveOverlayScheme(BestEfficiency) == 0)
                overlay = current;
        }
        catch (EntryPointNotFoundException) { }

        // Lower the APU's power limit. A running game session keeps the original limits in RyzenAdj's restore
        // file and re-applies its own on wake; otherwise we save the current limits first.
        var lowered = false;
        if (_ryzenAdj() is { } adj)
        {
            var ownSave = !_sessionOwnsPower();
            if (ownSave) adj.SaveOriginal();
            var w = Math.Clamp(_sleepWatts(), 4, 15);
            lowered = adj.Apply(w, w);
            if (!lowered && ownSave) adj.Restore();
        }
        Json.Save(RestoreFile, new Saved(overlay, lowered && !_sessionOwnsPower()));
        _log($"Sleep: low-power mode on{(lowered ? $" ({_sleepWatts()} W limit)" : "")}");
    }

    private void Leave()
    {
        lock (_lock)
        {
            if (!_asleep) return;
            _asleep = Asleep = false;
        }
        RestoreSaved();
        AsleepChanged?.Invoke(false);
    }

    private void RestoreSaved()
    {
        if (Json.Load<Saved>(RestoreFile) is not { } saved) return;
        try { if (saved.Overlay is { } g) PowerSetActiveOverlayScheme(g); }
        catch (EntryPointNotFoundException) { }
        if (saved.PowerLowered) _ryzenAdj()?.Restore();
        try { File.Delete(RestoreFile); } catch (IOException) { }
        _log("Woke: power mode and limits restored");
    }

    /// <summary>At start-up: undo what a crash during sleep left behind.</summary>
    public void RecoverIfNeeded() => RestoreSaved();

    /// <summary>
    /// Windows "hibernate after" (minutes asleep, 0 = never) for the active power plan. Turns hibernation on when
    /// needed (admin). Returns a message for the UI.
    /// </summary>
    public static string SetHibernateAfter(int minutes)
    {
        var seconds = Math.Max(0, minutes) * 60;
        if (seconds > 0) Run("/hibernate on");
        var ok = Run($"/setacvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE {seconds}") && Run($"/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE {seconds}") && Run("/setactive SCHEME_CURRENT");
        return !ok ? "Couldn't change the hibernate timer (run the launcher as admin)."
             : seconds == 0 ? "Windows won't hibernate from sleep." : $"After {minutes} min asleep Windows hibernates: fans and battery drain stop completely; it resumes where you were.";
    }

    private static bool Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public void Dispose()
    {
        _events.Changed -= OnPower;
        _events.Dispose();
        if (_asleep) RestoreSaved();
    }
}
