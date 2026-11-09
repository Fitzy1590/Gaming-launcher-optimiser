using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

public sealed record PowerLimits(double StapmW, double FastW, double SlowW)
{
    public string Text => $"{StapmW:0} W sustained / {FastW:0} W boost";
}

/// <summary>
/// Sets the APU's power limits through RyzenAdj (the open-source tool handheld utilities use; you download
/// it once). The original limits are read first and saved to disk, so they're restored after the game,
/// and on the next start if the PC crashed. Vendor apps like OneXConsole may re-apply their own limit; if
/// so, turn off TDP control there while the launcher manages it.
/// </summary>
public sealed partial class RyzenAdjClient(string exePath, AppPaths paths)
{
    private string RestoreFile => Path.Combine(paths.Root, "power-restore.json");

    public static string? Find(string? configured)
    {
        if (configured is not null && File.Exists(configured)) return configured;
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(pf, "RyzenAdj", "ryzenadj.exe"),
            Path.Combine(pf, "Handheld Companion", "ryzenadj", "ryzenadj.exe"),
            Path.Combine(pf, "Handheld Companion", "Resources", "RyzenAdj", "ryzenadj.exe"),
            @"C:\RyzenAdj\ryzenadj.exe",
            @"C:\Tools\RyzenAdj\ryzenadj.exe",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"\|\s*(STAPM LIMIT|PPT LIMIT FAST|PPT LIMIT SLOW)\s*\|\s*([\d.]+)\s*\|", RegexOptions.IgnoreCase)]
    private static partial Regex InfoLine();

    public PowerLimits? Read()
    {
        var (code, output) = Run("-i");
        if (code != 0) return null;
        double? stapm = null, fast = null, slow = null;
        foreach (Match m in InfoLine().Matches(output))
        {
            var v = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            switch (m.Groups[1].Value.ToUpperInvariant())
            {
                case "STAPM LIMIT": stapm = v; break;
                case "PPT LIMIT FAST": fast = v; break;
                case "PPT LIMIT SLOW": slow = v; break;
            }
        }
        return stapm is { } s ? new PowerLimits(s, fast ?? s, slow ?? s) : null;
    }

    /// <summary>What RyzenAdj said the last time a set failed (shown by Settings → Auto TDP → Check).</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Sustained = watts; short boost a little higher so loading screens stay quick.</summary>
    public bool Apply(int watts, int maxWatts)
    {
        var sustained = watts * 1000;
        var boost = Math.Min((int)(watts * 1.15), maxWatts) * 1000;
        return SetLimits(sustained, sustained, boost);
    }

    public bool Apply(PowerLimits l) => SetLimits((int)(l.StapmW * 1000), (int)(l.SlowW * 1000), (int)(l.FastW * 1000));

    /// <summary>
    /// Sets the three limits. Some chips reject one of the three and RyzenAdj then exits non-zero even though the
    /// others took, so a failure is retried one limit at a time and the result is judged by reading the limits back.
    /// </summary>
    private bool SetLimits(int stapmMw, int slowMw, int fastMw)
    {
        LastError = "";
        var all = Run($"--stapm-limit={stapmMw} --slow-limit={slowMw} --fast-limit={fastMw}");
        if (all.code == 0) return true;
        var notes = new List<string> { $"all three together: exit {all.code} {Trim(all.output)}" };
        foreach (var (name, mw) in new[] { ("stapm-limit", stapmMw), ("slow-limit", slowMw), ("fast-limit", fastMw) })
        {
            var one = Run($"--{name}={mw}");
            if (one.code != 0) notes.Add($"{name}: exit {one.code} {Trim(one.output)}");
        }
        // The sustained limit is the one that matters: judge by what the chip reports now.
        var now = Read();
        if (now is not null && Math.Abs(now.StapmW - stapmMw / 1000.0) <= 0.6) return true;
        LastError = string.Join("; ", notes) + (now is null ? "" : $"; chip still reports {now.StapmW:0.#} W sustained");
        return false;
    }

    private static string Trim(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 120 ? line[..120] : line;
    }

    private string UndervoltMarker => Path.Combine(paths.Root, "undervolt-active");

    /// <summary>
    /// All-core Curve Optimizer offset (negative = undervolt). A marker file is written first, so if the
    /// PC crashes or freezes before it's cleared, the next start knows and backs the offset off.
    /// </summary>
    public bool ApplyUndervolt(int offset)
    {
        File.WriteAllText(UndervoltMarker, offset.ToString(CultureInfo.InvariantCulture));
        return Run($"--set-coall={FitzLauncher.Core.Optimization.UndervoltLadder.Encode(offset)}").code == 0;
    }

    public void ClearUndervolt()
    {
        Run("--set-coall=0");
        try { File.Delete(UndervoltMarker); } catch (IOException) { }
    }

    /// <summary>True when the last undervolted session never finished (crash, freeze, power loss).</summary>
    public bool UndervoltCrashedLastTime() => File.Exists(UndervoltMarker);

    public void ClearCrashMarker()
    {
        try { File.Delete(UndervoltMarker); } catch (IOException) { }
    }

    /// <summary>Reads and stores the current limits so they can be put back.</summary>
    public PowerLimits? SaveOriginal()
    {
        var current = Read();
        if (current is not null) Json.Save(RestoreFile, current);
        return current;
    }

    public void Restore()
    {
        var original = Json.Load<PowerLimits>(RestoreFile);
        if (original is not null && Apply(original)) File.Delete(RestoreFile);
    }

    /// <summary>At launcher start: put the power limits back if a crash left them lowered.</summary>
    public void RecoverIfNeeded()
    {
        if (File.Exists(RestoreFile)) Restore();
    }

    private (int code, string output) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.ExitCode, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, e.Message); }
    }
}
