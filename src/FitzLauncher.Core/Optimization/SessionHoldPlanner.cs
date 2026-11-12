using System.Globalization;
using System.Text.Json;

namespace FitzLauncher.Core.Optimization;

/// <summary>One Defender setting Fitz changed for the session: its name, the value before, and the value Fitz set.</summary>
public sealed class HoldChange
{
    public string Name { get; set; } = "";
    public string Previous { get; set; } = "";
    public string Applied { get; set; } = "";
}

/// <summary>What the Windows side read before the session (Defender scan settings and the drive-optimise task).</summary>
public sealed class HoldSnapshot
{
    public bool DefenderReadable { get; set; }
    public bool? ScanOnlyIfIdleEnabled { get; set; }
    public int? ScanAvgCPULoadFactor { get; set; }
    public bool? EnableLowCpuPriority { get; set; }
    public bool? DisableCatchupFullScan { get; set; }
    public bool? DisableCatchupQuickScan { get; set; }
    /// <summary>State of the scheduled drive-optimise task: Ready, Running, Disabled… or null if it doesn't exist.</summary>
    public string? DefragState { get; set; }

    internal string? Value(string name) => name switch
    {
        SessionHoldPlanner.IdleOnly => Fmt(ScanOnlyIfIdleEnabled),
        SessionHoldPlanner.CpuLoad => ScanAvgCPULoadFactor?.ToString(CultureInfo.InvariantCulture),
        SessionHoldPlanner.LowPriority => Fmt(EnableLowCpuPriority),
        SessionHoldPlanner.NoCatchupFull => Fmt(DisableCatchupFullScan),
        SessionHoldPlanner.NoCatchupQuick => Fmt(DisableCatchupQuickScan),
        _ => null,
    };

    private static string? Fmt(bool? b) => b is null ? null : b.Value ? "True" : "False";
}

public sealed record MaintenancePlan(bool StopRunningTask, bool DisableTask)
{
    public bool IsEmpty => !StopRunningTask && !DisableTask;
}

/// <summary>
/// Decides what to change while a game runs so background scans and maintenance stay out of the way, and
/// what to put back afterwards. Pure: the Windows side reads the snapshot, runs the scripts and checks the result.
/// Real-time protection is never read or written. Settings Fitz doesn't own, or that someone changed during the
/// session, are left alone.
/// </summary>
public static class SessionHoldPlanner
{
    public const string IdleOnly = "ScanOnlyIfIdleEnabled";
    public const string CpuLoad = "ScanAvgCPULoadFactor";
    public const string LowPriority = "EnableLowCpuPriority";
    public const string NoCatchupFull = "DisableCatchupFullScan";
    public const string NoCatchupQuick = "DisableCatchupQuickScan";

    public const int DeferredCpuLoad = 20;
    public const string DefragTaskPath = @"\Microsoft\Windows\Defrag\";
    public const string DefragTaskName = "ScheduledDefrag";

    private static readonly string[] Allowed = [IdleOnly, CpuLoad, LowPriority, NoCatchupFull, NoCatchupQuick];

    public const string LeftAloneMessage =
        "Windows Defender is managed by your organisation or protected by Tamper Protection, so background scans were left alone.";

    // ---------------------------------------------------------------- reading

    /// <summary>PowerShell that prints the snapshot as one line of JSON. Needs no admin rights.</summary>
    public const string ReadScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $o = [ordered]@{ DefenderReadable = $false }
        $p = Get-MpPreference
        if ($p) {
          $o.DefenderReadable = $true
          $o.ScanOnlyIfIdleEnabled = [bool]$p.ScanOnlyIfIdleEnabled
          $o.ScanAvgCPULoadFactor = [int]$p.ScanAvgCPULoadFactor
          $o.EnableLowCpuPriority = [bool]$p.EnableLowCpuPriority
          $o.DisableCatchupFullScan = [bool]$p.DisableCatchupFullScan
          $o.DisableCatchupQuickScan = [bool]$p.DisableCatchupQuickScan
        }
        $t = Get-ScheduledTask -TaskPath '\Microsoft\Windows\Defrag\' -TaskName 'ScheduledDefrag'
        if ($t) { $o.DefragState = [string]$t.State }
        $o | ConvertTo-Json -Compress
        """;

    /// <summary>Reads the last JSON line of the script's output; null when it isn't a snapshot.</summary>
    public static HoldSnapshot? ParseSnapshot(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));
        if (line is null) return null;
        try { return JsonSerializer.Deserialize<HoldSnapshot>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }

    // ---------------------------------------------------------------- Defender

    /// <summary>The settings to change: only the ones that need changing, and only ones that could be read.</summary>
    public static List<HoldChange> PlanDefender(HoldSnapshot s)
    {
        var list = new List<HoldChange>();
        if (!s.DefenderReadable) return list;

        Add(IdleOnly, "True");
        // 1-20 is already at least as gentle as ours; 0 means "no limit".
        if (s.ScanAvgCPULoadFactor is { } load && (load is < 1 or > DeferredCpuLoad)) Add(CpuLoad, DeferredCpuLoad.ToString(CultureInfo.InvariantCulture));
        Add(LowPriority, "True");
        Add(NoCatchupFull, "True");
        Add(NoCatchupQuick, "True");
        return list;

        void Add(string name, string target)
        {
            var current = s.Value(name);
            if (current is not null && current != target) list.Add(new HoldChange { Name = name, Previous = current, Applied = target });
        }
    }

    /// <summary>The recorded changes that are still exactly as Fitz set them. Used to keep only what stuck, and to restore only what is ours.</summary>
    public static List<HoldChange> StillApplied(IEnumerable<HoldChange> changes, HoldSnapshot now) =>
        changes.Where(c => Allowed.Contains(c.Name) && now.Value(c.Name) == c.Applied).ToList();

    /// <summary>Script that sets each change's Applied value (apply = true) or Previous value (apply = false).</summary>
    public static string BuildDefenderScript(IEnumerable<HoldChange> changes, bool apply)
    {
        var lines = new List<string> { "$ErrorActionPreference = 'Stop'" };
        foreach (var c in changes)
        {
            if (!Allowed.Contains(c.Name)) continue;
            var v = Literal(apply ? c.Applied : c.Previous);
            if (v is null) continue;
            lines.Add($"try {{ Set-MpPreference -{c.Name} {v} }} catch {{ }}");
        }
        return string.Join('\n', lines);
    }

    private static string? Literal(string v) =>
        v == "True" ? "$true" : v == "False" ? "$false"
        : int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 100 ? n.ToString(CultureInfo.InvariantCulture)
        : null;

    // ------------------------------------------------------------- maintenance

    public static MaintenancePlan PlanMaintenance(HoldSnapshot s) => s.DefragState switch
    {
        "Running" => new MaintenancePlan(StopRunningTask: true, DisableTask: true),
        "Ready" or "Queued" => new MaintenancePlan(StopRunningTask: false, DisableTask: true),
        _ => new MaintenancePlan(false, false), // missing, already disabled, or unknown: not ours to touch
    };

    /// <summary>
    /// Disables the drive-optimise task (stopping it first if it is running) and asks Automatic Maintenance to stop.
    /// MSchedExe is checked for first; a missing file or a failure is ignored.
    /// </summary>
    public static string BuildMaintenanceScript(MaintenancePlan plan)
    {
        var task = $"-TaskPath '{DefragTaskPath}' -TaskName '{DefragTaskName}'";
        var lines = new List<string> { "$ErrorActionPreference = 'Stop'" };
        if (plan.DisableTask) lines.Add($"try {{ Disable-ScheduledTask {task} | Out-Null }} catch {{ }}");
        if (plan.StopRunningTask) lines.Add($"try {{ Stop-ScheduledTask {task} }} catch {{ }}");
        lines.Add("try { $m = Join-Path $env:windir 'System32\\MSchedExe.exe'; if (Test-Path $m) { & $m Stop | Out-Null } } catch { }");
        return string.Join('\n', lines);
    }

    /// <summary>Only re-enable the task if Fitz disabled it and it is still disabled (nobody else touched it).</summary>
    public static bool ShouldReenableDefrag(bool weDisabled, HoldSnapshot now) =>
        weDisabled && now.DefragState == "Disabled";

    public static string BuildReenableScript() =>
        $"$ErrorActionPreference = 'Stop'\ntry {{ Enable-ScheduledTask -TaskPath '{DefragTaskPath}' -TaskName '{DefragTaskName}' | Out-Null }} catch {{ }}";
}
