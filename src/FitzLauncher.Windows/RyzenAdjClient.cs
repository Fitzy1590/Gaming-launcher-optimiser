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

    // What this chip accepts, learned on the first change and reused: every RyzenAdj run talks to the chip's power controller,
    // and several runs in a row (a retry storm) can hitch the whole system.
    private HashSet<string>? _accepted;
    private bool _exitCodeUnreliable;

    /// <summary>
    /// Sets the three limits in ONE RyzenAdj run. The first time, if that run reports a failure, each limit is tried alone once to learn
    /// which ones this chip accepts (some reject one of the three and then exit non-zero although the others took); after that only the
    /// accepted ones are sent, so a power change is always a single run.
    /// </summary>
    private bool SetLimits(int stapmMw, int slowMw, int fastMw)
    {
        LastError = "";
        var flags = new[] { ("stapm-limit", stapmMw), ("slow-limit", slowMw), ("fast-limit", fastMw) };
        string Args(IEnumerable<(string name, int mw)> f) => string.Join(' ', f.Select(x => $"--{x.name}={x.mw}"));

        if (_accepted is { } known)
        {
            var args = Args(flags.Where(f => known.Contains(f.Item1)));
            if (args.Length == 0) return false;
            var r = Run(args);
            if (r.code == 0 || _exitCodeUnreliable) return true;
            LastError = $"exit {r.code} {Trim(r.output)}";
            return false;
        }

        var all = Run(Args(flags));
        if (all.code == 0) { _accepted = [.. flags.Select(f => f.Item1)]; return true; }

        var notes = new List<string> { $"all three together: exit {all.code} {Trim(all.output)}" };
        var ok = new HashSet<string>();
        foreach (var f in flags)
        {
            var one = Run(Args([f]));
            if (one.code == 0) ok.Add(f.Item1);
            else notes.Add($"{f.Item1}: exit {one.code} {Trim(one.output)}");
        }
        if (ok.Count > 0) { _accepted = ok; return ok.Contains("stapm-limit"); }

        // Nothing reported success. Some builds exit non-zero even when it worked: judge once by reading the chip back.
        var now = Read();
        if (now is not null && Math.Abs(now.StapmW - stapmMw / 1000.0) <= 0.6)
        {
            _accepted = [.. flags.Select(f => f.Item1)];
            _exitCodeUnreliable = true;
            return true;
        }
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

    private DateTime _pausedUntil = DateTime.MinValue;

    /// <summary>
    /// Runs RyzenAdj with a hard time limit. It talks to the chip's power controller and on some systems a run can hang; the output is read
    /// in the background so the wait can time out, a hung run is killed, and RyzenAdj is left alone for 30 s afterwards instead of being retried.
    /// </summary>
    private (int code, string output) Run(string args)
    {
        if (DateTime.UtcNow < _pausedUntil) return (-3, "RyzenAdj is paused for a moment after it stopped answering");
        try
        {
            var psi = new ProcessStartInfo(exePath, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "couldn't start RyzenAdj");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5_000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                _pausedUntil = DateTime.UtcNow.AddSeconds(30);
                return (-2, "RyzenAdj didn't answer within 5 s and was stopped");
            }
            var output = Task.WaitAll([outTask, errTask], 1_000) ? outTask.Result + errTask.Result : "";
            return (p.ExitCode, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, e.Message); }
    }
}
