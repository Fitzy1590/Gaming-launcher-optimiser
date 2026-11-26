namespace FitzLauncher.Core.Audio;

public enum AudioProfile { Off, Clarity, Footsteps }

/// <summary>
/// Text for Equalizer APO (SPEC §17.3). Fitz owns one file (<see cref="FitzFile"/>) and one <c>Include:</c> line in the user's
/// config.txt; nothing else in config.txt is touched. Filter syntax is from Equalizer APO's documentation (VERIFY on device).
/// </summary>
public static class EqualizerApo
{
    public const string FitzFile = "fitz-profile.txt";
    private const string Marker = "# Fitz Launcher (removed when the game closes)";

    public static IReadOnlyList<string> ProfileLines(AudioProfile p) => p switch
    {
        // Lifts speech and detail a little; the preamp stops the boost clipping.
        AudioProfile.Clarity => ["Preamp: -3 dB", "Filter: ON PK Fc 3000 Hz Gain 3 dB Q 1.0", "Filter: ON HSC Fc 8000 Hz Gain 2 dB"],
        // Brings out footsteps and reloads, trims rumble that masks them.
        AudioProfile.Footsteps => ["Preamp: -4 dB", "Filter: ON LSC Fc 120 Hz Gain -4 dB", "Filter: ON PK Fc 2500 Hz Gain 4 dB Q 1.2", "Filter: ON PK Fc 5000 Hz Gain 2 dB Q 1.5"],
        _ => [],
    };

    public static string IncludeLine => "Include: " + FitzFile;

    public static bool HasInclude(string config) => Lines(config).Any(IsOurs);

    /// <summary>Adds the marker and Include line at the end. No change if it is already there.</summary>
    public static string AddInclude(string config)
    {
        if (HasInclude(config)) return config;
        var nl = config.Contains("\r\n") ? "\r\n" : "\n";
        var body = config.Length > 0 && !config.EndsWith('\n') ? config + nl : config;
        return body + Marker + nl + IncludeLine + nl;
    }

    /// <summary>Removes only Fitz's marker and Include line, so edits the user made meanwhile stay.</summary>
    public static string RemoveInclude(string config)
    {
        var nl = config.Contains("\r\n") ? "\r\n" : "\n";
        var all = Lines(config).ToList();
        if (all.Count > 0 && all[^1] == "") all.RemoveAt(all.Count - 1); // from the final newline
        var kept = all.Where(l => !IsOurs(l) && l.Trim() != Marker);
        var text = string.Join(nl, kept);
        return config.EndsWith('\n') && text.Length > 0 ? text + nl : text;
    }

    private static bool IsOurs(string line) => line.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase);
    private static string[] Lines(string s) => s.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
}
