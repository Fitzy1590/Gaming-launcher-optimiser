using System.Text;

namespace FitzLauncher.Core.Diagnostics;

/// <summary>Builds the one-file diagnostic export. Every line starts with its section in brackets so it can be searched with a plain text filter.</summary>
public static class DiagnosticPackage
{
    public static string Build(DateTimeOffset at, IEnumerable<(string Section, IEnumerable<string> Lines)> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[header] Fitz Launcher diagnostic package, {at:yyyy-MM-dd HH:mm:ss zzz}");
        foreach (var (section, lines) in sections)
            foreach (var line in lines)
                foreach (var part in line.Split('\n'))
                    sb.AppendLine($"[{section}] {part.TrimEnd('\r')}");
        return LogRedactor.Redact(sb.ToString());
    }
}
