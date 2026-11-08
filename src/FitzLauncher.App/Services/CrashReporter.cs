using System.Windows;
using FitzLauncher.Core.Diagnostics;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.App.Services;

/// <summary>
/// Catches errors anywhere in the launcher and explains them in plain English with fix links. Recoverable
/// errors show a window and the launcher carries on; fatal ones are written to disk first and shown on the
/// next start. A "running" marker spots crashes that kill the process outright (detected at the next start,
/// with the faulting module from Windows' error log).
/// </summary>
public static class CrashReporter
{
    private static AppPaths? _paths;
    private static DateTime _lastShown;
    private static string Marker => Path.Combine(_paths!.Root, "running.marker");
    private static string LastCrash => Path.Combine(_paths!.Root, "last-crash.txt");
    private static string StartupCrashes => Path.Combine(_paths!.Root, "startup-crashes.txt");

    /// <summary>
    /// Two crashes in a row while starting: start with the extras off (overlays, controller remapping, cooling,
    /// undervolt, background checks) so the launcher opens and the cause can be found.
    /// </summary>
    public static bool SafeMode { get; private set; }
    public static string Version => UpdateService.CurrentVersion.ToString();
    public static string Os => $"Windows {Environment.OSVersion.Version} {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}";

    /// <summary>Call first thing at start-up. Returns a report about the previous run if it crashed.</summary>
    public static (PlainError error, string report)? Start(AppPaths paths)
    {
        _paths = paths;
        (PlainError, string)? previous = null;
        try
        {
            if (File.Exists(LastCrash))
            {
                var text = File.ReadAllText(LastCrash);
                previous = (new PlainError("The launcher crashed last time",
                    "It stopped because of an error, and here's what was recorded.",
                    "If it happens again, use Report the problem. Installing the latest version often fixes it.",
                    [new FixLink("Download the latest version", ErrorExplainer.ReleasesUrl)]), text);
                File.Delete(LastCrash);
            }
            else if (File.Exists(Marker))
            {
                var since = new DateTimeOffset(File.GetLastWriteTimeUtc(Marker), TimeSpan.Zero);
                var module = FitzLauncher.Windows.CrashDetector.FaultingModule("FitzLauncher.exe", since);
                var graphics = module is not null && (module.StartsWith("amd", StringComparison.OrdinalIgnoreCase) || module.StartsWith("nv", StringComparison.OrdinalIgnoreCase) || module.StartsWith("igd", StringComparison.OrdinalIgnoreCase) || module.Contains("d3d", StringComparison.OrdinalIgnoreCase));
                previous = (new PlainError("The launcher closed unexpectedly last time",
                    module is null ? "It stopped without reporting an error (power loss, a forced close, or a crash Windows didn't log)."
                                   : $"Windows recorded a crash in {module}.",
                    graphics ? "That's a graphics driver file. Update the graphics driver, then try again."
                             : "If it keeps happening, use Report the problem and try the latest version.",
                    graphics ? [new FixLink("Get AMD drivers", "https://www.amd.com/en/support/download/drivers.html"), new FixLink("Get NVIDIA drivers", "https://www.nvidia.com/Download/index.aspx")]
                             : [new FixLink("Download the latest version", ErrorExplainer.ReleasesUrl)]),
                    $"Fitz Launcher {Version} · {Os}\nPrevious session started {since.ToLocalTime():yyyy-MM-dd HH:mm} and didn't close normally.\nFaulting module: {module ?? "not recorded"}");
            }
            // "starting" until the window has been up for a while; a crash before that counts as a start-up crash.
            var crashedStarting = File.Exists(Marker) && File.ReadAllText(Marker).StartsWith("starting", StringComparison.Ordinal);
            var count = crashedStarting && int.TryParse(File.Exists(StartupCrashes) ? File.ReadAllText(StartupCrashes) : "0", out var n) ? n + 1 : 0;
            File.WriteAllText(StartupCrashes, count.ToString());
            SafeMode = count >= 2;
            File.WriteAllText(Marker, "starting " + DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return previous;
    }

    /// <summary>Call once the launcher has been running normally for a while: start-up succeeded.</summary>
    public static void MarkStarted()
    {
        try
        {
            if (_paths is null) return;
            File.WriteAllText(Marker, "running " + DateTimeOffset.Now.ToString("O"));
            if (!SafeMode) File.WriteAllText(StartupCrashes, "0");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Leave safe mode: forget the start-up crashes (takes effect on the next start).</summary>
    public static void ClearSafeMode()
    {
        try { if (_paths is not null) File.WriteAllText(StartupCrashes, "0"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Clean exit: no crash to report next time.</summary>
    public static void Stop()
    {
        try { if (_paths is not null) File.Delete(Marker); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A recoverable error on the UI thread: explain it and carry on (at most one window per 10 s).</summary>
    public static void ShowRecoverable(Exception e, string context, Action<string> log)
    {
        var report = ErrorExplainer.Report(e, Version, Os, context);
        log(report);
        if (DateTime.UtcNow - _lastShown < TimeSpan.FromSeconds(10)) return;
        _lastShown = DateTime.UtcNow;
        Show(ErrorExplainer.Explain(e), report, fatal: false);
    }

    public static void Show(PlainError error, string report, bool fatal)
    {
        var w = new Views.CrashWindow(error, report, _paths?.Root ?? Path.GetTempPath(), fatal);
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && owner != w) w.Owner = owner;
        w.ShowDialog();
    }

    /// <summary>A fatal error (any thread): save the report for the next start and tell the user now if possible.</summary>
    public static void Fatal(Exception e, string context)
    {
        var report = ErrorExplainer.Report(e, Version, Os, context);
        try { if (_paths is not null) File.WriteAllText(LastCrash, report); } catch (Exception io) when (io is IOException or UnauthorizedAccessException) { }
        var plain = ErrorExplainer.Explain(e);
        try
        {
            MessageBox.Show($"{plain.Title}\n\n{plain.WhatHappened}\n\nWhat to do: {plain.WhatToDo}\n\nThe full report will be shown next time the launcher starts.",
                "Fitz Launcher has to close", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception) { /* nothing more we can do */ }
    }
}
