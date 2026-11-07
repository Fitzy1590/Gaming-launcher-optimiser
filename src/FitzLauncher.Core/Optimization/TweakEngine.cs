using FitzLauncher.Core.Config;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>Registry access, implemented by the Windows layer (and faked in tests).</summary>
public interface IRegistryAccess
{
    string? GetString(RegistryHive hive, string key, string name);
    void Set(RegistryHive hive, string key, string name, string value, RegistryValueType type);
    void Delete(RegistryHive hive, string key, string name);
}

public sealed record ApplyResult(Tweak Tweak, bool Success, string Message);

/// <summary>
/// Applies and reverts tweaks. Every change records the value it replaced the first time a target
/// is touched, so re-optimising (e.g. 117 → 100 FPS) and then reverting restores the true original.
/// Edited files are also backed up once before the first change.
/// </summary>
public sealed class TweakEngine(IRegistryAccess registry, string backupRoot)
{
    public ApplyResult Apply(List<AppliedChange> ledger, Tweak tweak, Game? game = null)
    {
        if (!tweak.IsAutomatic) return new ApplyResult(tweak, false, "Manual step: follow the instructions.");
        try
        {
            foreach (var action in tweak.Actions) ApplyAction(ledger, tweak, action, game);
            return new ApplyResult(tweak, true, "Applied");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return new ApplyResult(tweak, false, e.Message);
        }
    }

    public IReadOnlyList<string> RevertAll(List<AppliedChange> ledger, Game? game = null, string? tweakId = null)
    {
        var errors = new List<string>();
        // Newest first, so stacked edits unwind in order.
        for (var i = ledger.Count - 1; i >= 0; i--)
        {
            var change = ledger[i];
            if (tweakId is not null && change.TweakId != tweakId) continue;
            try
            {
                Revert(change, game);
                ledger.RemoveAt(i);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                errors.Add($"{change.Title}: {e.Message}");
            }
        }
        return errors;
    }

    public static bool IsApplied(IEnumerable<AppliedChange> ledger, Tweak tweak) =>
        tweak.IsAutomatic && tweak.Actions.All(a => ledger.Any(c => TargetKey(c.Action) == TargetKey(a) && ValueOf(c.Action) == ValueOf(a)));

    // ------------------------------------------------------------------ apply

    private void ApplyAction(List<AppliedChange> ledger, Tweak tweak, TweakAction action, Game? game)
    {
        var existing = ledger.FirstOrDefault(c => TargetKey(c.Action) == TargetKey(action));
        string? previous;
        var existed = true;
        string? backup = null;

        switch (action)
        {
            case IniSetAction ini:
            {
                var dir = Path.GetDirectoryName(ini.FilePath)!;
                if (!Directory.Exists(dir)) throw new IOException($"Config folder not found: {dir}");
                if (!File.Exists(ini.FilePath) && !ini.CreateIfMissing) throw new IOException($"{Path.GetFileName(ini.FilePath)} not found; launch the game once first.");
                backup = BackupOnce(ledger, ini.FilePath, game);
                var doc = IniDocument.Load(ini.FilePath);
                previous = doc.Set(ini.Section, ini.Key, ini.Value);
                existed = previous is not null;
                WritePreservingReadOnly(ini.FilePath, doc.ToString());
                break;
            }
            case KeyValueFileSetAction kv:
            {
                if (!File.Exists(kv.FilePath)) throw new IOException($"{kv.FilePath} not found.");
                backup = BackupOnce(ledger, kv.FilePath, game);
                var doc = KeyValueFile.Load(kv.FilePath);
                previous = doc.Set(kv.Key, kv.Value);
                existed = previous is not null;
                WritePreservingReadOnly(kv.FilePath, doc.ToString());
                break;
            }
            case RegistrySetAction reg:
            {
                previous = registry.GetString(reg.Hive, reg.KeyPath, reg.ValueName);
                existed = previous is not null;
                var value = reg.MergeSemicolonPairs ? MergePairs(previous, reg.Value) : reg.Value;
                registry.Set(reg.Hive, reg.KeyPath, reg.ValueName, value, reg.ValueType);
                break;
            }
            case LaunchArgumentAction args:
            {
                if (game is null) throw new InvalidOperationException("Launch options need a game.");
                previous = game.OptimizerArguments;
                game.OptimizerArguments = ArgumentString.Upsert(game.OptimizerArguments, args.Arguments);
                break;
            }
            case RtssFrameLimitAction rtss:
            {
                var profiles = Path.Combine(rtss.RtssDirectory, "Profiles");
                if (!Directory.Exists(profiles)) throw new IOException("RTSS Profiles folder not found.");
                var file = Path.Combine(profiles, rtss.ExeName + ".cfg");
                existed = File.Exists(file);
                var doc = IniDocument.Load(file);
                previous = doc.Set("Framerate", "Limit", rtss.Limit.ToString());
                if (!existed) previous = null;
                doc.Save(file);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported action {action.GetType().Name}");
        }

        if (existing is not null)
        {
            // Keep the original "previous" value; just update what is currently applied.
            existing.Action = action;
            existing.AppliedAt = DateTimeOffset.Now;
            return;
        }
        ledger.Add(new AppliedChange
        {
            TweakId = tweak.Id,
            Title = tweak.Title,
            Action = action,
            PreviousValue = previous,
            PreviouslyExisted = existed,
            BackupPath = backup,
        });
    }

    // ------------------------------------------------------------------ revert

    private void Revert(AppliedChange change, Game? game)
    {
        switch (change.Action)
        {
            case IniSetAction ini when File.Exists(ini.FilePath):
            {
                var doc = IniDocument.Load(ini.FilePath);
                if (change.PreviouslyExisted && change.PreviousValue is not null) doc.Set(ini.Section, ini.Key, change.PreviousValue);
                else doc.Remove(ini.Section, ini.Key);
                WritePreservingReadOnly(ini.FilePath, doc.ToString());
                break;
            }
            case KeyValueFileSetAction kv when File.Exists(kv.FilePath):
            {
                var doc = KeyValueFile.Load(kv.FilePath);
                if (change.PreviouslyExisted && change.PreviousValue is not null) doc.Set(kv.Key, change.PreviousValue);
                else doc.Remove(kv.Key);
                WritePreservingReadOnly(kv.FilePath, doc.ToString());
                break;
            }
            case RegistrySetAction reg:
            {
                if (reg.MergeSemicolonPairs)
                {
                    // Only take our pair back out; other tools may have added pairs since.
                    var current = registry.GetString(reg.Hive, reg.KeyPath, reg.ValueName);
                    var restored = RestorePairs(current, reg.Value, change.PreviousValue);
                    if (string.IsNullOrEmpty(restored)) registry.Delete(reg.Hive, reg.KeyPath, reg.ValueName);
                    else registry.Set(reg.Hive, reg.KeyPath, reg.ValueName, restored, reg.ValueType);
                }
                else if (change.PreviouslyExisted && change.PreviousValue is not null)
                    registry.Set(reg.Hive, reg.KeyPath, reg.ValueName, change.PreviousValue, reg.ValueType);
                else
                    registry.Delete(reg.Hive, reg.KeyPath, reg.ValueName);
                break;
            }
            case LaunchArgumentAction args when game is not null:
                game.OptimizerArguments = ArgumentString.Remove(game.OptimizerArguments, args.Arguments);
                break;
            case RtssFrameLimitAction rtss:
            {
                var file = Path.Combine(rtss.RtssDirectory, "Profiles", rtss.ExeName + ".cfg");
                if (!File.Exists(file)) break;
                if (!change.PreviouslyExisted) { File.Delete(file); break; }
                var doc = IniDocument.Load(file);
                if (change.PreviousValue is not null) doc.Set("Framerate", "Limit", change.PreviousValue);
                else doc.Remove("Framerate", "Limit");
                doc.Save(file);
                break;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static string TargetKey(TweakAction a) => a switch
    {
        IniSetAction i => $"ini|{i.FilePath.ToLowerInvariant()}|{i.Section.ToLowerInvariant()}|{i.Key.ToLowerInvariant()}",
        KeyValueFileSetAction k => $"kv|{k.FilePath.ToLowerInvariant()}|{k.Key.ToLowerInvariant()}",
        // For merged DirectX prefs, each pair is its own target.
        RegistrySetAction r => $"reg|{r.Hive}|{r.KeyPath.ToLowerInvariant()}|{r.ValueName.ToLowerInvariant()}" +
                               (r.MergeSemicolonPairs ? "|" + r.Value.Split('=')[0].ToLowerInvariant() : ""),
        LaunchArgumentAction l => $"args|{ArgumentString.Key(l.Arguments)}",
        RtssFrameLimitAction r => $"rtss|{r.ExeName.ToLowerInvariant()}",
        _ => a.Describe(),
    };

    private static string ValueOf(TweakAction a) => a switch
    {
        IniSetAction i => i.Value,
        KeyValueFileSetAction k => k.Value,
        RegistrySetAction r => r.Value,
        LaunchArgumentAction l => l.Arguments,
        RtssFrameLimitAction r => r.Limit.ToString(),
        _ => "",
    };

    /// <summary>Merges "A=1;" into an existing "B=2;A=0;" style value.</summary>
    internal static string MergePairs(string? existing, string add)
    {
        var pairs = ParsePairs(existing);
        foreach (var (k, v) in ParsePairs(add)) pairs[k] = v;
        return string.Concat(pairs.Select(p => $"{p.Key}={p.Value};"));
    }

    internal static string RestorePairs(string? current, string ours, string? original)
    {
        var pairs = ParsePairs(current);
        var originalPairs = ParsePairs(original);
        foreach (var (k, _) in ParsePairs(ours))
        {
            if (originalPairs.TryGetValue(k, out var orig)) pairs[k] = orig;
            else pairs.Remove(k);
        }
        return string.Concat(pairs.Select(p => $"{p.Key}={p.Value};"));
    }

    private static Dictionary<string, string> ParsePairs(string? s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(s)) return d;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) d[part[..eq]] = part[(eq + 1)..];
        }
        return d;
    }

    private string? BackupOnce(List<AppliedChange> ledger, string file, Game? game)
    {
        if (!File.Exists(file)) return null;
        var already = ledger.FirstOrDefault(c => c.BackupPath is not null && c.Action is IniSetAction i && i.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase)
                                                 || c.BackupPath is not null && c.Action is KeyValueFileSetAction k && k.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (already is not null) return already.BackupPath;
        var dir = Path.Combine(backupRoot, game?.Id.ToString() ?? "system");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"{Path.GetFileName(file)}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
        File.Copy(file, dest, overwrite: true);
        return dest;
    }

    /// <summary>Some games ship config files read-only; edit them and put the attribute back.</summary>
    private static void WritePreservingReadOnly(string path, string content)
    {
        var readOnly = File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
        if (readOnly) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.WriteAllText(path, content);
        if (readOnly) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
    }
}

/// <summary>Launch-argument helpers. "+fps_max 117" is treated as one option keyed by "+fps_max".</summary>
public static class ArgumentString
{
    public static string Key(string arg) => arg.Trim().Split(' ', 2)[0].ToLowerInvariant();

    public static string Upsert(string existing, string arg)
    {
        var without = RemoveKey(existing, Key(arg));
        return string.IsNullOrWhiteSpace(without) ? arg.Trim() : without + " " + arg.Trim();
    }

    public static string Remove(string existing, string arg) => RemoveKey(existing, Key(arg));

    private static string RemoveKey(string existing, string key)
    {
        var tokens = existing.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var count = 1;
            // "+cvar value" consumes the next token as its value.
            if (key.StartsWith('+') && i + 1 < tokens.Count && tokens[i + 1][0] is not ('+' or '-')) count = 2;
            tokens.RemoveRange(i, count);
            i--;
        }
        return string.Join(' ', tokens);
    }
}
