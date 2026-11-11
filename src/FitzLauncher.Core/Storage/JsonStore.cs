using System.Text.Json;
using System.Text.Json.Serialization;

namespace FitzLauncher.Core.Storage;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(fs, Options);
        }
        catch (JsonException)
        {
            // Keep the broken file for inspection rather than silently losing the user's library.
            File.Copy(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
            return null;
        }
    }

    /// <summary>Write-to-temp then rename, so a crash mid-save never leaves a truncated file.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) JsonSerializer.Serialize(fs, value, Options);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Locations of all launcher data. Everything lives under one folder so it's easy to back up.</summary>
public sealed class AppPaths(string root)
{
    public static AppPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FitzLauncher"));

    public string Root { get; } = root;
    public string Library => Path.Combine(Root, "library.json");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Hardware => Path.Combine(Root, "hardware.json");
    public string Sessions => Path.Combine(Root, "sessions.jsonl");
    public string GameModeState => Path.Combine(Root, "gamemode-restore.json");
    /// <summary>Background apps the resource guard changed (priorities, paused apps), so a crash can't leave them that way.</summary>
    public string ResourceGuardState => Path.Combine(Root, "resource-guard-restore.json");
    /// <summary>Resource guard choices and the history of what helped.</summary>
    public string ResourceGuardMemory => Path.Combine(Root, "resource-guard.json");
    public string Benchmarks => Path.Combine(Root, "benchmarks.jsonl");
    public string Presets => Path.Combine(Root, "presets.json");
    public string SaveBackupDir => Path.Combine(Root, "save-backups");
    public string ArtworkDir => Path.Combine(Root, "artwork");
    public string BackupDir => Path.Combine(Root, "backups");
    public string LogFile => Path.Combine(Root, "launcher.log");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ArtworkDir);
        Directory.CreateDirectory(BackupDir);
    }
}
