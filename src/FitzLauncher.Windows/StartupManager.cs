using System.Diagnostics;
using System.Security.Principal;
using System.Security;

namespace FitzLauncher.Windows;

/// <summary>
/// Opening without a UAC prompt and starting with Windows, done the standard way: Task Scheduler
/// tasks that run "with highest privileges". The task definitions use XML, so they work on any
/// Windows language and date format. Turning an option off deletes its task and shortcut.
/// </summary>
public static class StartupManager
{
    public const string OnDemandTask = "FitzLauncher";
    public const string LogonTask = "FitzLauncher (startup)";

    public static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Fitz Launcher.lnk");

    /// <summary>Creates the elevated on-demand task plus a desktop shortcut that runs it (no UAC prompt).</summary>
    public static (bool ok, string message) EnableNoUac(string exePath)
    {
        var (ok, msg) = CreateTask(OnDemandTask, exePath, "", logonTrigger: false);
        if (!ok) return (false, msg);
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(DesktopShortcut);
            sc.TargetPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            sc.Arguments = $"/run /tn \"{OnDemandTask}\"";
            sc.IconLocation = exePath + ",0";
            sc.WindowStyle = 7; // minimised: no console flash
            sc.Description = "Fitz Launcher (opens without the admin prompt)";
            sc.Save();
        }
        catch (Exception e) { return (false, "Task created, but the shortcut failed: " + e.Message); }
        return (true, "Desktop shortcut \"Fitz Launcher\" now opens it without the admin prompt.");
    }

    public static void DisableNoUac()
    {
        DeleteTask(OnDemandTask);
        try { if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut); } catch (IOException) { }
    }

    public static (bool ok, string message) SetStartWithWindows(bool enabled, string exePath)
    {
        if (!enabled) { DeleteTask(LogonTask); return (true, "Won't start with Windows."); }
        var r = CreateTask(LogonTask, exePath, "--minimized", logonTrigger: true);
        return r.ok ? (true, "Starts minimised when you sign in.") : r;
    }

    private static (bool ok, string message) CreateTask(string name, string exe, string args, bool logonTrigger)
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var trigger = logonTrigger
            ? $"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>"
            : "<Triggers />";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Fitz Launcher</Description></RegistrationInfo>
              {trigger}
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{SecurityElement.Escape(args)}</Arguments><WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe)!)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"fitz-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            var (code, output) = RunSchtasks($"/Create /TN \"{name}\" /XML \"{file}\" /F");
            return code == 0 ? (true, "OK") : (false, output.Trim());
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    private static void DeleteTask(string name) => RunSchtasks($"/Delete /TN \"{name}\" /F");

    public static bool TaskExists(string name) => RunSchtasks($"/Query /TN \"{name}\"").code == 0;

    private static (int code, string output) RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception e) { return (-1, e.Message); }
    }
}
