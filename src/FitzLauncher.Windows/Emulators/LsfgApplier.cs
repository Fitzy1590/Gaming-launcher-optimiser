using FitzLauncher.Core.Emulators;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows.Emulators;

/// <summary>
/// Sets Lossless Scaling to 2x for a session and puts the old values back afterwards (and at the next start after a
/// crash). Key-level patch of its Settings.xml with a backup and a restore record written first. Files only: no hooks,
/// no injection. Settings.xml location is from memory, unverified until a device test (SPEC §15).
/// </summary>
public sealed class LsfgApplier(AppPaths paths)
{
    private sealed record Saved(string File, List<Lsfg.SettingChange> Changes);
    private string RestoreFile => Path.Combine(paths.Root, "lsfg-restore.json");
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lossless Scaling", "Settings.xml");

    /// <summary>A plain-English line for the session log, or null when Lossless Scaling is not set up here.</summary>
    public string? Apply(LsfgPlan plan)
    {
        var file = SettingsPath;
        if (!File.Exists(file)) return "Lossless Scaling: settings file not found, so 2x was not set. Open Lossless Scaling once and try again.";
        try
        {
            var (xml, changes) = Lsfg.Patch(File.ReadAllText(file), plan);
            if (changes.Count == 0) return "Lossless Scaling: already set to 2x, or this version's settings are not recognised yet (nothing changed).";
            File.Copy(file, file + ".fitz.bak", overwrite: true);
            Json.Save(RestoreFile, new Saved(file, changes)); // record first
            var tmp = file + ".fitz.tmp";
            File.WriteAllText(tmp, xml);
            File.Move(tmp, file, overwrite: true);
            return $"Lossless Scaling: {plan.Summary}. Press its scale hotkey in the game window.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Lossless Scaling: could not change its settings (" + e.Message + ").";
        }
    }

    public void Restore()
    {
        if (Json.Load<Saved>(RestoreFile) is not { } s) return;
        try
        {
            if (File.Exists(s.File)) File.WriteAllText(s.File, Lsfg.Revert(File.ReadAllText(s.File), s.Changes));
            File.Delete(RestoreFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep the record; retried at next start */ }
    }

    public static void RecoverIfNeeded(AppPaths paths) => new LsfgApplier(paths).Restore();
}
