namespace FitzLauncher.Core.Config;

/// <summary>
/// Minimal, lossless INI editor. Unlike most INI libraries it keeps every line it doesn't touch
/// (comments, ordering, blank lines, duplicate "+Key=" array entries used by Unreal), so a tweak
/// changes exactly one line and can be reverted exactly.
/// </summary>
public sealed class IniDocument
{
    private readonly List<string> _lines;
    private readonly string _newline;

    private IniDocument(List<string> lines, string newline)
    {
        _lines = lines;
        _newline = newline;
    }

    public static IniDocument Parse(string text)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1); // trailing newline
        return new IniDocument(lines, newline);
    }

    public static IniDocument Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : Parse("");

    public void Save(string path) => File.WriteAllText(path, ToString());

    public override string ToString() => _lines.Count == 0 ? "" : string.Join(_newline, _lines) + _newline;

    public IEnumerable<string> Sections =>
        _lines.Select(l => l.Trim()).Where(IsSectionHeader).Select(l => l[1..^1].Trim());

    public string? Get(string section, string key)
    {
        var idx = FindKey(section, key);
        return idx < 0 ? null : ValueOf(_lines[idx]);
    }

    /// <summary>Returns all key/value pairs of a section (last one wins for duplicates).</summary>
    public Dictionary<string, string> GetSection(string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (start, end) = SectionRange(section);
        if (start < 0) return result;
        for (var i = start + 1; i < end; i++)
        {
            if (TryParseKey(_lines[i], out var k)) result[k] = ValueOf(_lines[i]);
        }
        return result;
    }

    /// <summary>Sets a value, creating the section/key if needed. Returns the previous value (null if absent).</summary>
    public string? Set(string section, string key, string value)
    {
        var idx = FindKey(section, key);
        if (idx >= 0)
        {
            var previous = ValueOf(_lines[idx]);
            _lines[idx] = $"{key}={value}";
            return previous;
        }

        var (start, end) = SectionRange(section);
        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return null;
        }

        // Insert after the last non-blank line of the section, so blank separators stay where they were.
        var insertAt = end;
        while (insertAt - 1 > start && _lines[insertAt - 1].Trim().Length == 0) insertAt--;
        _lines.Insert(insertAt, $"{key}={value}");
        return null;
    }

    /// <summary>Removes a key. Returns true if it existed. Leaves an emptied section header in place.</summary>
    public bool Remove(string section, string key)
    {
        var idx = FindKey(section, key);
        if (idx < 0) return false;
        _lines.RemoveAt(idx);
        return true;
    }

    private int FindKey(string section, string key)
    {
        var (start, end) = SectionRange(section);
        if (start < 0) return -1;
        // Last occurrence wins in most engines, so edit that one.
        for (var i = end - 1; i > start; i--)
        {
            if (TryParseKey(_lines[i], out var k) && k.Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private (int start, int end) SectionRange(string section)
    {
        var start = -1;
        for (var i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Trim();
            if (!IsSectionHeader(t)) continue;
            if (start >= 0) return (start, i);
            if (t[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) start = i;
        }
        return start < 0 ? (-1, -1) : (start, _lines.Count);
    }

    private static bool IsSectionHeader(string trimmed) =>
        trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']';

    private static bool TryParseKey(string line, out string key)
    {
        key = "";
        var t = line.TrimStart();
        if (t.Length == 0 || t[0] is ';' or '#' or '[') return false;
        var eq = t.IndexOf('=');
        if (eq <= 0) return false;
        key = t[..eq].Trim();
        // Unreal array syntax (+Key / -Key / .Key) is never what we edit.
        return key.Length > 0 && key[0] is not ('+' or '-' or '.' or '!');
    }

    private static string ValueOf(string line)
    {
        var eq = line.IndexOf('=');
        return eq < 0 ? "" : line[(eq + 1)..].Trim();
    }
}
