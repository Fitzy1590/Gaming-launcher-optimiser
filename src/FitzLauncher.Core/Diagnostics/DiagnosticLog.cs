using System.Text;
using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Diagnostics;

/// <summary>
/// Optional trace kept in memory only (SPEC §17 G3). Off by default: while off, <see cref="Write"/> returns at once
/// (no allocation, no disk). Every line is passed through <see cref="Sanitize"/> before it is stored.
/// </summary>
public sealed partial class DiagnosticLog(int capacity = 500)
{
    private readonly Queue<string> _lines = new();
    private readonly Lock _lock = new();
    private volatile bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) Clear(); }
    }

    public void Write(string message)
    {
        if (!_enabled) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {Sanitize(message)}";
        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity) _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> Snapshot() { lock (_lock) return [.. _lines]; }
    public void Clear() { lock (_lock) _lines.Clear(); }

    /// <summary>Hides values that follow a secret-looking name (key, token, password, cookie, SWID, ESPN_S2…) and user folder names.</summary>
    public static string Sanitize(string text) =>
        ErrorExplainer.Redact(SecretValue().Replace(text, "$1=[REDACTED]"));

    /// <summary>One plain-text report: facts, then the trace. Sanitised again as a whole.</summary>
    public static string BuildPackage(string header, IEnumerable<(string Name, string Value)> facts, IEnumerable<string> trace, bool traceWasOn)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine($"Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine().AppendLine("== System ==");
        foreach (var (name, value) in facts) sb.AppendLine($"{name}: {value}");
        sb.AppendLine().AppendLine("== Trace ==");
        if (!traceWasOn) sb.AppendLine("Diagnostic trace was off, so there is nothing recorded. Turn it on in Settings, play, then export again.");
        foreach (var line in trace) sb.AppendLine(line);
        return Sanitize(sb.ToString());
    }

    // name, then ':' or '=' (or a space for "Bearer abc"), then the value up to whitespace, ';' or '&'.
    [GeneratedRegex(@"(?i)\b(api[_-]?key|apikey|access[_-]?token|token|passkey|passwd|password|secret|swid|espn_s2|cookie|authorization|bearer)(?:\s*[:=]\s*|\s+)(?:bearer\s+)?(?:""[^""]*""|[^\s;&,""]+)")]
    private static partial Regex SecretValue();
}
