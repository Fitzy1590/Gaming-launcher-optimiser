namespace FitzLauncher.Core.Config;

/// <summary>Lossless editor for flat "key=value" files such as Unity's boot.config.</summary>
public sealed class KeyValueFile
{
    private readonly List<string> _lines;
    private readonly string _newline;

    private KeyValueFile(List<string> lines, string newline)
    {
        _lines = lines;
        _newline = newline;
    }

    public static KeyValueFile Parse(string text)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return new KeyValueFile(lines, newline);
    }

    public static KeyValueFile Load(string path) => Parse(File.Exists(path) ? File.ReadAllText(path) : "");

    public void Save(string path) => File.WriteAllText(path, ToString());

    public override string ToString() => _lines.Count == 0 ? "" : string.Join(_newline, _lines) + _newline;

    public string? Get(string key)
    {
        var i = IndexOf(key);
        return i < 0 ? null : _lines[i][(_lines[i].IndexOf('=') + 1)..].Trim();
    }

    public string? Set(string key, string value)
    {
        var i = IndexOf(key);
        if (i < 0)
        {
            _lines.Add($"{key}={value}");
            return null;
        }
        var previous = Get(key);
        _lines[i] = $"{key}={value}";
        return previous;
    }

    public bool Remove(string key)
    {
        var i = IndexOf(key);
        if (i < 0) return false;
        _lines.RemoveAt(i);
        return true;
    }

    private int IndexOf(string key)
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            var eq = _lines[i].IndexOf('=');
            if (eq > 0 && _lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
