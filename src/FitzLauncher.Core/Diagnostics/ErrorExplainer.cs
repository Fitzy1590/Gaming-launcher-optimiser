using System.Text;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Updates;

namespace FitzLauncher.Core.Diagnostics;

public sealed record FixLink(string Label, string Url);

/// <summary>An error in plain English: what happened, what to do, and links that fix it.</summary>
public sealed record PlainError(string Title, string WhatHappened, string WhatToDo, List<FixLink> Fixes);

/// <summary>
/// Turns exceptions into plain-English explanations with fix links (reinstall the latest version, install the
/// Visual C++ runtime, free disk space, check antivirus, report the problem), and builds a crash report with
/// the Windows user name hidden.
/// </summary>
public static partial class ErrorExplainer
{
    public static readonly string ReleasesUrl = $"https://github.com/{ReleaseInfo.Repository}/releases/latest";
    public const string VcRedistX64 = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    public const string VcRedistX86 = "https://aka.ms/vs/17/release/vc_redist.x86.exe";

    private static readonly FixLink Reinstall = new("Download the latest version", ReleasesUrl);
    private static readonly FixLink VcRedist = new("Install Visual C++ runtime (Microsoft)", VcRedistX64);
    private static readonly FixLink Defender = new("Open Windows Security protection history", "windowsdefender://threathistory");
    private static readonly FixLink Storage = new("Free up disk space", "ms-settings:storagesense");
    private static readonly FixLink Network = new("Check network status", "ms-settings:network-status");

    /// <summary>The most specific exception: unwraps aggregate, invocation and XAML wrappers.</summary>
    public static Exception Root(Exception e)
    {
        while (true)
        {
            switch (e)
            {
                case AggregateException { InnerExceptions.Count: > 0 } a: e = a.InnerExceptions[0]; continue;
                case System.Reflection.TargetInvocationException { InnerException: { } i }: e = i; continue;
                case TypeInitializationException { InnerException: { } t }: e = t; continue;
            }
            if (e.GetType().FullName == "System.Windows.Markup.XamlParseException" && e.InnerException is { } x) { e = x; continue; }
            return e;
        }
    }

    public static PlainError Explain(Exception original)
    {
        var e = Root(original);
        var hr = e.HResult & 0xFFFF;
        return e switch
        {
            DllNotFoundException or BadImageFormatException => new("A system component is missing",
                $"The launcher needed a Windows component that isn't installed or is damaged ({Short(e.Message)}).",
                "Install the Microsoft Visual C++ runtime, then run the device check in Settings. If it keeps happening, download the latest launcher again (antivirus sometimes removes a file).",
                [VcRedist, Reinstall, Defender]),
            FileNotFoundException f when f.FileName?.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) == true => new("A launcher file is missing",
                $"{Path.GetFileName(f.FileName)} wasn't found. The download may be incomplete, or antivirus removed it.",
                "Download the latest version and unzip it over this one (your library and settings are kept). Check Windows Security's protection history for removed files.",
                [Reinstall, Defender]),
            TypeLoadException or MissingMethodException or MissingFieldException => new("Launcher files don't match",
                "Some of the launcher's files are from different versions, usually after an interrupted update.",
                "Use Settings → Reinstall latest version, or download the latest zip and unzip it over this folder.",
                [Reinstall]),
            UnauthorizedAccessException or System.Security.SecurityException => new("Windows blocked access",
                $"The launcher wasn't allowed to change something ({Short(e.Message)}).",
                "Run the launcher as administrator (or turn on \"Start without the UAC prompt\" in Settings). If a game folder is in Program Files, antivirus 'controlled folder access' can also block it.",
                [Defender]),
            IOException when hr is 0x70 or 0x27 => new("The disk is full",
                "There isn't enough free space to finish what the launcher was doing.",
                "Free some space on the drive, then try again.",
                [Storage]),
            IOException when hr is 0x20 or 0x21 => new("A file is in use",
                $"Another program has a file open ({Short(e.Message)}). A running game, trainer or antivirus scan is the usual cause.",
                "Close the game and any tools using its files, then try again.",
                []),
            IOException when hr is 0xE1 or 0xE2 => new("Antivirus removed a file",
                "Windows Security (or your antivirus) blocked or removed a file the launcher was using.",
                "Open Windows Security → Protection history. If you trust the file, allow it, then try again.",
                [Defender]),
            OutOfMemoryException or InsufficientExecutionStackException => new("Ran out of memory",
                "Windows didn't have enough free memory for the launcher.",
                "Close other programs (or the game) and reopen the launcher.",
                []),
            _ when e.GetType().FullName == "System.Net.Http.HttpRequestException" || e is TimeoutException || e.GetType().Name == "TaskCanceledException" => new("Couldn't connect",
                "The launcher couldn't reach an online service (updates, artwork, mods or game versions).",
                "Check the internet connection and try again. Everything that doesn't need the internet keeps working.",
                [Network]),
            _ => new("Something went wrong",
                $"The launcher hit an unexpected error: {Short(e.Message)}",
                "Try again. If it keeps happening, copy the report and send it in; installing the latest version often fixes it.",
                [Reinstall]),
        };
    }

    public static string Report(Exception e, string version, string os, string context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Fitz Launcher {version} · {os}");
        sb.AppendLine($"When: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · {context}");
        var plain = Explain(e);
        sb.AppendLine($"Summary: {plain.Title}. {plain.WhatHappened}");
        sb.AppendLine();
        sb.AppendLine(e.ToString());
        return Redact(sb.ToString());
    }

    /// <summary>A GitHub "new issue" link with the report filled in (shortened to fit a URL).</summary>
    public static string IssueUrl(string title, string report)
    {
        var body = report.Length > 1800 ? report[..1800] + "\n…(truncated; paste the full report from the clipboard)" : report;
        return $"https://github.com/{ReleaseInfo.Repository}/issues/new?title={Uri.EscapeDataString("Crash: " + title)}&body={Uri.EscapeDataString("```\n" + body + "\n```")}";
    }

    public static string Redact(string text) => UserPath().Replace(text, "$1<user>");

    private static string Short(string message) => message.Length > 160 ? message[..160] + "…" : message;

    [GeneratedRegex(@"(?i)([A-Z]:[\\/]+Users[\\/]+)[^\\/\s]+")] private static partial Regex UserPath();
}

public enum CheckStatus { Ok, Info, Recommended, Required }

/// <summary>One line of the device check.</summary>
public sealed record DeviceCheckItem(string Name, CheckStatus Status, string Detail, FixLink? Fix = null, string? InstallerUrl = null)
{
    /// <summary>Required/Recommended items that aren't satisfied.</summary>
    public bool NeedsAction => Status is CheckStatus.Required or CheckStatus.Recommended;
}
