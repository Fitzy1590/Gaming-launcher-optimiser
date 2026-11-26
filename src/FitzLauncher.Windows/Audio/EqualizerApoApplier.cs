using System.Runtime.InteropServices;
using FitzLauncher.Core.Audio;
using FitzLauncher.Core.Storage;
using Microsoft.Win32;

namespace FitzLauncher.Windows.Audio;

/// <summary>What was changed in Equalizer APO's config folder, saved before the change so a crash can be undone.</summary>
public sealed class EqualizerApoRestore
{
    public string ConfigDir { get; set; } = "";
}

/// <summary>
/// Applies a sound profile through Equalizer APO while a game runs: writes Fitz's own file, adds one Include line to config.txt
/// (backup first), and takes both away again at the end. Needs Equalizer APO installed and write access to its config folder
/// (it lives in Program Files, so that normally means running as admin). SPEC §17.3.
/// </summary>
public sealed class EqualizerApoApplier(AppPaths paths, Action<string>? log = null)
{
    /// <summary>Config folder from the installer's registry key, then the default install path. Both are VERIFY on device.</summary>
    public static string? FindConfigDir()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
            if (k?.GetValue("InstallPath") is string p && Directory.Exists(Path.Combine(p, "config"))) return Path.Combine(p, "config");
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO", "config");
        return Directory.Exists(def) ? def : null;
    }

    public bool HasRecord => File.Exists(paths.EqualizerApoState);

    /// <summary>Returns a plain-English line, or null when nothing was changed.</summary>
    public string? Apply(AudioProfile profile)
    {
        if (profile == AudioProfile.Off) return null;
        var dir = FindConfigDir();
        if (dir is null) { log?.Invoke("Audio profile skipped: Equalizer APO was not found."); return null; }
        var config = Path.Combine(dir, "config.txt");
        try
        {
            if (!File.Exists(config)) return null;
            var original = File.ReadAllText(config);
            Json.Save(paths.EqualizerApoState, new EqualizerApoRestore { ConfigDir = dir }); // record first
            File.Copy(config, config + ".fitz.bak", overwrite: true);
            WriteAtomic(Path.Combine(dir, EqualizerApo.FitzFile), string.Join(Environment.NewLine, EqualizerApo.ProfileLines(profile)) + Environment.NewLine);
            WriteAtomic(config, EqualizerApo.AddInclude(original));
            return $"Sound profile: {profile} (Equalizer APO; removed when the game closes)";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("Audio profile not applied: " + e.Message + " (Equalizer APO's folder usually needs admin).");
            Revert(); // leave nothing half-done
            return null;
        }
    }

    /// <summary>Removes Fitz's include line and file. Only Fitz's lines are removed, so edits made during play are kept.</summary>
    public void Revert()
    {
        var rec = Json.Load<EqualizerApoRestore>(paths.EqualizerApoState);
        if (rec is null) return;
        try
        {
            var config = Path.Combine(rec.ConfigDir, "config.txt");
            if (File.Exists(config))
            {
                var text = File.ReadAllText(config);
                if (EqualizerApo.HasInclude(text)) WriteAtomic(config, EqualizerApo.RemoveInclude(text));
            }
            File.Delete(Path.Combine(rec.ConfigDir, EqualizerApo.FitzFile));
            File.Delete(paths.EqualizerApoState);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("Could not remove the sound profile yet (will retry at next start): " + e.Message);
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".fitz.tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
