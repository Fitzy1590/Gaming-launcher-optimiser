using System.IO.Pipes;
using Microsoft.Win32;

namespace FitzLauncher.App.Services.Mods;

/// <summary>
/// Makes the launcher the handler for Nexus "Mod Manager Download" (nxm://) links, for this Windows user
/// only (HKCU). The previous handler (Vortex, MO2) is remembered and put back when you turn it off.
/// A second launcher process started by a link hands the link to the running one over a named pipe.
/// </summary>
public static class NxmHandler
{
    private const string Key = @"Software\Classes\nxm";
    private const string PipeName = "FitzLauncher.nxm";

    public static string OurCommand => $"\"{Environment.ProcessPath}\" --nxm \"%1\"";

    public static string? CurrentCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key + @"\shell\open\command");
        return k?.GetValue(null) as string;
    }

    public static bool IsOurs() => CurrentCommand()?.Contains(Path.GetFileName(Environment.ProcessPath ?? "FitzLauncher.exe"), StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Registers us; returns the command that was there before (to restore later).</summary>
    public static string? Register()
    {
        var previous = IsOurs() ? null : CurrentCommand();
        using var root = Registry.CurrentUser.CreateSubKey(Key);
        root.SetValue(null, "URL:NXM Protocol");
        root.SetValue("URL Protocol", "");
        using var cmd = Registry.CurrentUser.CreateSubKey(Key + @"\shell\open\command");
        cmd.SetValue(null, OurCommand);
        return previous;
    }

    public static void Unregister(string? previous)
    {
        if (!IsOurs()) return;
        if (previous is { Length: > 0 })
        {
            using var cmd = Registry.CurrentUser.CreateSubKey(Key + @"\shell\open\command");
            cmd.SetValue(null, previous);
        }
        else Registry.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
    }

    /// <summary>The nxm:// link passed on the command line, if any.</summary>
    public static string? FromArgs(string[] args)
    {
        var i = Array.FindIndex(args, a => a.Equals("--nxm", StringComparison.OrdinalIgnoreCase));
        var link = i >= 0 && i + 1 < args.Length ? args[i + 1] : args.FirstOrDefault(a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
        return link is not null && NxmLink.Parse(link) is not null ? link : null;
    }

    /// <summary>Second instance: send the link to the running launcher.</summary>
    public static bool SendToRunning(string link)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var w = new StreamWriter(pipe);
            w.WriteLine(link);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException) { return false; }
    }

    /// <summary>Running launcher: receive links from later instances.</summary>
    public static async Task ListenAsync(Action<string> onLink, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var r = new StreamReader(pipe);
                var line = await r.ReadLineAsync(ct);
                if (line is not null && NxmLink.Parse(line.Trim()) is not null) onLink(line.Trim());
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(500, CancellationToken.None); }
        }
    }
}
