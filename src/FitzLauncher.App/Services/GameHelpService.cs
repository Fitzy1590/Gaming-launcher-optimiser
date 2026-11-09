using System.Diagnostics;
using FitzLauncher.Windows;

namespace FitzLauncher.App.Services;

/// <summary>
/// AI Game Companion (github.com/Wintersta7e/AiGameCompanion, MIT): a separate transparent overlay that answers
/// questions about the game, optionally with a screenshot. Fitz opens it once the game window is up (without taking
/// focus), drives its hotkeys from PS combos, and closes it with the game if Fitz started it.
/// </summary>
public sealed class GameHelpService(AppServices s)
{
    public const string ReleasesUrl = "https://github.com/Wintersta7e/AiGameCompanion/releases";

    /// <summary>The companion's launcher.exe: the path set in Settings, else the usual install folders.</summary>
    public string? Locate()
    {
        if (s.Settings.GameHelpPath is { Length: > 0 } p && File.Exists(p)) return p;
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d).Replace(" ", "").Contains("GameCompanion", StringComparison.OrdinalIgnoreCase)))
                    if (Directory.EnumerateFiles(dir, "launcher.exe", SearchOption.AllDirectories).FirstOrDefault() is { } exe) return exe;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>A running copy of the companion (by path), or null.</summary>
    private static Process? Running(string exe)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            try { if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return p; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
        return null;
    }

    /// <summary>
    /// Opens the companion once the game's window exists. Returns the process if Fitz started it (so it can be
    /// closed with the game), or null if it was already running or couldn't start.
    /// </summary>
    public async Task<Process?> StartWithGameAsync(Func<IReadOnlyCollection<int>> gamePids, Action<string> status, CancellationToken ct)
    {
        if (Locate() is not { } exe) { status("Game help: AI Game Companion isn't installed (Settings → Game help)."); return null; }
        try
        {
            var until = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < until && ForegroundWindow.Find(gamePids()) == 0) await Task.Delay(1000, ct);
            await Task.Delay(3000, ct);
        }
        catch (TaskCanceledException) { return null; }
        if (Running(exe) is { } already) { already.Dispose(); status("Game help ready: PS + Triangle (or Ctrl+Shift+G)."); return null; }
        try
        {
            var gameWindow = ForegroundWindow.Find(gamePids());
            var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            // Keep the game in front: the companion stays hidden until its hotkey.
            if (p is not null && gameWindow != 0)
            {
                await ForegroundWindow.WaitForWindowAsync(p, TimeSpan.FromSeconds(10), ct);
                ForegroundWindow.Activate(gameWindow);
            }
            status("Game help ready: PS + Triangle shows the AI Game Companion panel (Ctrl+Shift+G).");
            return p;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            status("Couldn't start AI Game Companion: " + e.Message);
            return null;
        }
    }

    public static void Stop(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) { p.CloseMainWindow(); if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true); } }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { p.Dispose(); }
    }
}
