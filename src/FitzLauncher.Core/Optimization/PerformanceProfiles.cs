namespace FitzLauncher.Core.Optimization;

/// <summary>The one-tap profile switch on the game panel (SPEC §18, vote "reuse").</summary>
public enum PerformanceProfile { Turbo, Balanced, BatterySaver }

/// <summary>
/// A shortcut onto settings the game already has: each profile only writes <c>Game.Cooling</c> and, on APUs,
/// <c>Game.AutoTdp</c>. The session's existing appliers (and their restore records) do the rest, so there is no
/// second path to any setting and nothing changes until the next Play.
/// </summary>
public static class PerformanceProfiles
{
    /// <summary>What a profile writes. A null Auto TDP means "my default" (the global setting).</summary>
    public static (CoolingMode Cooling, AutoTdpMode? AutoTdp) For(PerformanceProfile profile) => profile switch
    {
        PerformanceProfile.Turbo => (CoolingMode.Off, AutoTdpMode.Off),
        PerformanceProfile.BatterySaver => (CoolingMode.Coolest, AutoTdpMode.On),
        _ => (CoolingMode.Balanced, null),
    };

    /// <summary>The profile the game's settings match, or null for a custom mix.</summary>
    /// <param name="cooling">The game's own cooling mode; null falls back to <paramref name="defaultCooling"/>.</param>
    /// <param name="managesPower">False on non-APU PCs, where Auto TDP doesn't apply and is ignored.</param>
    public static PerformanceProfile? Match(CoolingMode? cooling, CoolingMode defaultCooling, AutoTdpMode? autoTdp, bool managesPower)
    {
        var effective = cooling ?? defaultCooling;
        foreach (var p in Enum.GetValues<PerformanceProfile>())
        {
            var (c, a) = For(p);
            if (c == effective && (!managesPower || a == autoTdp)) return p;
        }
        return null;
    }

    public static string Label(PerformanceProfile profile) => profile switch
    {
        PerformanceProfile.Turbo => "Turbo",
        PerformanceProfile.BatterySaver => "Battery saver",
        _ => "Balanced",
    };

    public static string Describe(PerformanceProfile? profile, bool managesPower) => profile switch
    {
        PerformanceProfile.Turbo => managesPower
            ? "Turbo: cooling off and Auto TDP off, so the chip runs at full power. Applies next time you press Play."
            : "Turbo: cooling off, so the chip runs at full performance. Applies next time you press Play.",
        PerformanceProfile.BatterySaver => managesPower
            ? "Battery saver: Coolest cooling and Auto TDP on, so power drops to the least that holds your FPS target. Applies next time you press Play."
            : "Battery saver: Coolest cooling, so the CPU boosts less eagerly. Applies next time you press Play.",
        PerformanceProfile.Balanced => "Balanced: Balanced cooling" + (managesPower ? " and your default Auto TDP setting." : ".") + " Applies next time you press Play.",
        _ => "Custom: this game's cooling and power settings don't match a profile. Pick one to replace them.",
    };
}
