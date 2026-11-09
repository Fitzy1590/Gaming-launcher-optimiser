using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Emulation;

public enum ConfigFormat { Ini, Toml, Yaml, Json }

/// <summary>Change one existing setting. <c>Path</c> is the section (INI/TOML) or the chain of parent keys (YAML/JSON); empty = top level.</summary>
public sealed record ConfigEdit(string[] Path, string Key, string Value);

public sealed record PatchOutcome(string Text, List<string> Applied, List<string> Skipped)
{
    public bool Changed => Applied.Count > 0;
}

/// <summary>
/// Changes settings in an emulator's config file without rewriting it: only keys that already exist are edited (a key a
/// version doesn't have is skipped, never invented) and only when the new value has the same kind as the old one
/// (number, true/false or text). Comments, order and line endings are kept. JSON is re-saved indented.
/// </summary>
public static class ConfigPatcher
{
    private enum Kind { Bool, Number, Text }

    public static PatchOutcome Patch(ConfigFormat format, string text, IReadOnlyList<ConfigEdit> edits) => format switch
    {
        ConfigFormat.Json => PatchJson(text, edits),
        ConfigFormat.Yaml => PatchLines(text, edits, yaml: true),
        _ => PatchLines(text, edits, yaml: false),
    };

    private static Kind KindOf(string v)
    {
        v = v.Trim().Trim('"', '\'');
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return Kind.Bool;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? Kind.Number : Kind.Text;
    }

    /// <summary>New text for a value: keeps the old one's quotes and True/true style.</summary>
    private static string? Render(string old, string value)
    {
        var trimmed = old.Trim();
        var quoted = trimmed.Length >= 2 && (trimmed[0] is '"' or '\'') && trimmed[^1] == trimmed[0];
        var oldKind = KindOf(old);
        var newKind = KindOf(value);
        if (oldKind != newKind) return null;
        if (oldKind == Kind.Bool)
        {
            var upper = trimmed.Trim('"', '\'').Length > 0 && char.IsUpper(trimmed.Trim('"', '\'')[0]);
            var b = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            var word = upper ? (b ? "True" : "False") : (b ? "true" : "false");
            return quoted ? $"{trimmed[0]}{word}{trimmed[0]}" : word;
        }
        return quoted ? $"{trimmed[0]}{value}{trimmed[0]}" : value;
    }

    private static readonly Regex Section = new(@"^\s*\[(?<n>[^\]]+)\]\s*(#.*|;.*)?$", RegexOptions.Compiled);
    private static readonly Regex YamlLine = new(@"^(?<indent>\s*)(?<key>[^\s#:][^:#]*?)\s*:(?<sep>\s*)(?<val>.*)$", RegexOptions.Compiled);

    private static (string value, string comment) SplitComment(string raw, bool allowHash)
    {
        if (!allowHash) return (raw, "");
        var inQuote = '\0';
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inQuote != '\0') { if (c == inQuote) inQuote = '\0'; continue; }
            if (c is '"' or '\'') inQuote = c;
            else if ((c == '#' || c == ';') && (i == 0 || char.IsWhiteSpace(raw[i - 1]))) return (raw[..i].TrimEnd(), raw[i..]);
        }
        return (raw, "");
    }

    private static PatchOutcome PatchLines(string text, IReadOnlyList<ConfigEdit> edits, bool yaml)
    {
        var lines = text.Split('\n');
        var applied = new List<string>();
        var skipped = new List<string>();

        foreach (var edit in edits)
        {
            var found = yaml ? FindYaml(lines, edit) : FindIni(lines, edit);
            if (found is null) { skipped.Add($"{Describe(edit)}: not in this version's settings file"); continue; }
            var (index, oldValue) = found.Value;
            var line = lines[index];
            var cr = line.EndsWith('\r') ? "\r" : "";
            var body = cr.Length > 0 ? line[..^1] : line;
            var newText = Render(oldValue, edit.Value);
            if (newText is null) { skipped.Add($"{Describe(edit)}: current value '{oldValue.Trim()}' isn't the same kind as '{edit.Value}'"); continue; }
            if (newText == oldValue.Trim()) { applied.Add($"{Describe(edit)} already {edit.Value}"); continue; }

            // Replace only the value part of the line.
            var at = body.IndexOf(oldValue.Trim(), body.IndexOf(yaml ? ':' : '=') + 1, StringComparison.Ordinal);
            if (at < 0) { skipped.Add($"{Describe(edit)}: couldn't locate the value"); continue; }
            lines[index] = body[..at] + newText + body[(at + oldValue.Trim().Length)..] + cr;
            applied.Add($"{Describe(edit)}: {oldValue.Trim()} → {newText}");

            if (!yaml) ClearQtDefault(lines, index, edit);
        }
        return new PatchOutcome(string.Join('\n', lines), applied, skipped);
    }

    private static string Describe(ConfigEdit e) => e.Path.Length == 0 ? e.Key : $"{string.Join(" › ", e.Path)} › {e.Key}";

    /// <returns>Line index and the current value text.</returns>
    private static (int, string)? FindIni(string[] lines, ConfigEdit edit)
    {
        var section = edit.Path.Length > 0 ? edit.Path[0] : "";
        var current = "";
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            if (Section.Match(l) is { Success: true } m) { current = m.Groups["n"].Value.Trim(); continue; }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var t = l.TrimStart();
            if (t.Length == 0 || t[0] is '#' or ';') continue;
            var eq = t.IndexOf('=');
            if (eq <= 0 || !t[..eq].Trim().Equals(edit.Key, StringComparison.OrdinalIgnoreCase)) continue;
            var (value, _) = SplitComment(t[(eq + 1)..].Trim(), allowHash: true);
            return (i, value);
        }
        return null;
    }

    /// <summary>Qt-style files keep "key\default=true" beside "key=…": the value only counts when default is false.</summary>
    private static void ClearQtDefault(string[] lines, int valueLine, ConfigEdit edit)
    {
        var section = edit.Path.Length > 0 ? edit.Path[0] : "";
        var current = "";
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            if (Section.Match(l) is { Success: true } m) { current = m.Groups["n"].Value.Trim(); continue; }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var t = l.TrimStart();
            if (!t.StartsWith(edit.Key + "\\default", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = t.IndexOf('=');
            if (eq < 0) continue;
            var cr = lines[i].EndsWith('\r') ? "\r" : "";
            lines[i] = l[..(l.IndexOf('=') + 1)] + "false" + cr;
        }
    }

    private static (int, string)? FindYaml(string[] lines, ConfigEdit edit)
    {
        var stack = new List<(int indent, string key)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            if (l.TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(l)) continue;
            if (YamlLine.Match(l) is not { Success: true } m) continue;
            var indent = m.Groups["indent"].Value.Length;
            var key = m.Groups["key"].Value.Trim().Trim('"', '\'');
            while (stack.Count > 0 && stack[^1].indent >= indent) stack.RemoveAt(stack.Count - 1);
            var parents = stack.Select(s => s.key).ToArray();
            var (value, _) = SplitComment(m.Groups["val"].Value, allowHash: true);
            if (key.Equals(edit.Key, StringComparison.OrdinalIgnoreCase) && value.Length > 0
                && parents.SequenceEqual(edit.Path, StringComparer.OrdinalIgnoreCase))
                return (i, value);
            stack.Add((indent, key));
        }
        return null;
    }

    private static PatchOutcome PatchJson(string text, IReadOnlyList<ConfigEdit> edits)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        JsonNode? root;
        try { root = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { return new PatchOutcome(text, [], ["The settings file isn't valid JSON, so nothing was changed"]); }
        if (root is null) return new PatchOutcome(text, [], ["The settings file is empty"]);

        foreach (var edit in edits)
        {
            JsonNode? node = root;
            foreach (var p in edit.Path) node = (node as JsonObject)?[p];
            if (node is not JsonObject obj || !obj.ContainsKey(edit.Key)) { skipped.Add($"{Describe(edit)}: not in this version's settings file"); continue; }
            var old = obj[edit.Key];
            switch (old)
            {
                case JsonValue v when v.TryGetValue<bool>(out var ob):
                    if (KindOf(edit.Value) != Kind.Bool) { skipped.Add($"{Describe(edit)}: expects true/false"); break; }
                    obj[edit.Key] = edit.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    applied.Add($"{Describe(edit)}: {ob.ToString().ToLowerInvariant()} → {edit.Value.ToLowerInvariant()}");
                    break;
                case JsonValue v when v.TryGetValue<double>(out var od):
                    if (!double.TryParse(edit.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nd)) { skipped.Add($"{Describe(edit)}: expects a number"); break; }
                    obj[edit.Key] = nd == Math.Floor(nd) && od == Math.Floor(od) ? (long)nd : nd;
                    applied.Add($"{Describe(edit)}: {od.ToString(CultureInfo.InvariantCulture)} → {nd.ToString(CultureInfo.InvariantCulture)}");
                    break;
                case JsonValue v when v.TryGetValue<string>(out var os):
                    obj[edit.Key] = edit.Value;
                    applied.Add($"{Describe(edit)}: {os} → {edit.Value}");
                    break;
                default: skipped.Add($"{Describe(edit)}: not a simple value"); break;
            }
        }
        return new PatchOutcome(applied.Count > 0 ? root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : text, applied, skipped);
    }
}
