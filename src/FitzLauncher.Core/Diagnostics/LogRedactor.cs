using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Diagnostics;

/// <summary>Removes secrets from text before it is written to a log or a diagnostic package (SPEC §17.3).</summary>
public static partial class LogRedactor
{
    public const string Mask = "[REDACTED]";

    /// <summary>Redacts keys, tokens, passwords and cookies (name=value, name: value, JSON "name":"value", Bearer tokens) and the Windows user name in paths.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Pair().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mask + m.Groups[4].Value);
        text = Bearer().Replace(text, "$1" + Mask);
        return ErrorExplainer.Redact(text);
    }

    // name, separator (: = or ":), value (quoted or up to a delimiter), closing quote kept
    [GeneratedRegex("""(?i)\b((?:api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|secret|token|passkey|password|passwd|authorization|set-cookie|cookie|swid|espn_s2)"?)(\s*[:=]\s*"?)((?:Bearer\s+)?[^\s",;&]+)("?)""")]
    private static partial Regex Pair();

    [GeneratedRegex(@"(?i)\b(Bearer\s+)[A-Za-z0-9._~+/=\-]+")]
    private static partial Regex Bearer();
}
