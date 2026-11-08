using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

/// <summary>
/// Applies a cooling mode's CPU boost setting for a session and puts the previous one back afterwards (and at
/// the next start if the launcher or PC crashed). Boost off keeps the CPU at its base clock, which is much
/// cooler; GPU-bound games barely notice.
/// </summary>
public sealed class CoolingController(AppPaths paths)
{
    private sealed record Saved(int Ac, int Dc);
    private string RestoreFile => Path.Combine(paths.Root, "cooling-restore.json");

    public bool ApplyBoost(int mode)
    {
        var (ac, dc) = PowerPlans.GetBoostMode();
        if (ac is null) return false;
        if (!File.Exists(RestoreFile)) Json.Save(RestoreFile, new Saved(ac.Value, dc ?? ac.Value));
        return PowerPlans.SetBoostMode(mode, mode);
    }

    public void Restore()
    {
        if (Json.Load<Saved>(RestoreFile) is { } s && PowerPlans.SetBoostMode(s.Ac, s.Dc))
            try { File.Delete(RestoreFile); } catch (IOException) { }
    }

    public static void RecoverIfNeeded(AppPaths paths) => new CoolingController(paths).Restore();
}
