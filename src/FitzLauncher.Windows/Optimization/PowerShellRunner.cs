using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace FitzLauncher.Windows;

/// <summary>Runs a short PowerShell script with no window and a hard timeout. Never throws.</summary>
internal static class PowerShellRunner
{
    public static (bool Ok, string Output) Run(string script, int timeoutMs, Action<string>? log = null)
    {
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(exe)) exe = "powershell.exe";
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand"); // base64 UTF-16: no quoting problems
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (false, "");
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
                log?.Invoke($"PowerShell timed out after {timeoutMs / 1000} s");
                return (false, "");
            }
            p.WaitForExit(); // flush the redirected streams
            return (p.ExitCode == 0, stdout.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            log?.Invoke("PowerShell could not run: " + e.Message);
            return (false, "");
        }
    }
}
