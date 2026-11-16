using System.ComponentModel;
using System.Diagnostics;
using FitzLauncher.Core.Emulators;
using Microsoft.Win32;

namespace FitzLauncher.Windows.Emulators;

public sealed record BlueStacksInstall(string InstallDir, string DataDir)
{
    public string PlayerPath => Path.Combine(InstallDir, BlueStacks.PlayerExe);
    public string AdbPath => Path.Combine(InstallDir, BlueStacks.AdbExe);
    public string ConfPath => Path.Combine(DataDir, "bluestacks.conf");
}

/// <summary>
/// Finds BlueStacks 5/10 and lists the Android apps inside it. Reads only: the registry key
/// (<c>HKLM\SOFTWARE\BlueStacks_nxt</c>), its config file and the output of its own adb tool. Registry value names
/// and the usual folders are from memory, unverified until a device test (SPEC §15).
/// </summary>
public static class BlueStacksLocator
{
    private const int ToolTimeoutMs = 15_000;

    public static BlueStacksInstall? Find()
    {
        string? install = null, data = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\BlueStacks_nxt");
            install = key?.GetValue("InstallDir") as string;
            data = key?.GetValue("DataDir") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }

        install ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BlueStacks_nxt");
        data ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BlueStacks_nxt");
        var found = new BlueStacksInstall(install, data);
        return File.Exists(found.PlayerPath) ? found : null;
    }

    public static List<BlueStacksInstance> Instances(BlueStacksInstall install)
    {
        try { return File.Exists(install.ConfPath) ? BlueStacks.ParseInstances(File.ReadAllText(install.ConfPath)) : []; }
        catch (IOException) { return []; }
    }

    /// <summary>
    /// The third-party apps installed in one instance. Needs "Android Debug Bridge" switched on in BlueStacks settings;
    /// returns an empty list (with a plain-English reason) when it is off or the emulator is not running.
    /// </summary>
    public static async Task<(List<AndroidApp> Apps, string? Problem)> ScanAppsAsync(BlueStacksInstall install, BlueStacksInstance instance, CancellationToken ct)
    {
        if (instance.AdbPort is not { } port) return ([], "BlueStacks does not list a debug port for this instance. Turn on Android Debug Bridge in BlueStacks settings (Advanced), then try again.");
        if (!File.Exists(install.AdbPath)) return ([], "BlueStacks' own adb tool was not found.");
        var serial = $"127.0.0.1:{port}";
        await RunAsync(install.AdbPath, $"connect {serial}", ct).ConfigureAwait(false);
        var output = await RunAsync(install.AdbPath, $"-s {serial} shell pm list packages -3", ct).ConfigureAwait(false);
        if (output is null) return ([], "Could not talk to BlueStacks. Open BlueStacks, wait for the home screen, and make sure Android Debug Bridge is on.");
        var apps = BlueStacks.ParsePackages(output);
        return (apps, apps.Count == 0 ? "No installed games found in that BlueStacks instance." : null);
    }

    private static async Task<string?> RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            var read = p.StandardOutput.ReadToEndAsync(ct);
            if (!p.WaitForExit(ToolTimeoutMs))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }
            return await read.ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}
