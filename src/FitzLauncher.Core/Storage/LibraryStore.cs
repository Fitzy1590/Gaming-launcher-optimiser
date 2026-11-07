using System.Text.Json;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Storage;

public sealed class LibraryData
{
    public int Version { get; set; } = 1;
    public List<Game> Games { get; set; } = [];
    /// <summary>Machine-wide tweaks applied from the Hardware page (revertible).</summary>
    public List<AppliedChange> SystemChanges { get; set; } = [];
}

/// <summary>Game library, settings and session history on disk.</summary>
public sealed class LibraryStore(AppPaths paths)
{
    private readonly Lock _sessionLock = new();

    public AppPaths Paths => paths;

    public LibraryData LoadLibrary() => Json.Load<LibraryData>(paths.Library) ?? new LibraryData();
    public void SaveLibrary(LibraryData data) => Json.Save(paths.Library, data);

    public LauncherSettings LoadSettings() => Json.Load<LauncherSettings>(paths.Settings) ?? new LauncherSettings();
    public void SaveSettings(LauncherSettings s) => Json.Save(paths.Settings, s);

    public HardwareProfile? LoadHardware() => Json.Load<HardwareProfile>(paths.Hardware);
    public void SaveHardware(HardwareProfile hw) => Json.Save(paths.Hardware, hw);

    /// <summary>Sessions are appended one JSON object per line: O(1) writes however long the history gets.</summary>
    public void AppendSession(SessionSummary s)
    {
        lock (_sessionLock)
        {
            Directory.CreateDirectory(paths.Root);
            File.AppendAllText(paths.Sessions, JsonSerializer.Serialize(s, Json.Compact) + Environment.NewLine);
        }
    }

    public List<SessionSummary> LoadSessions(Guid? gameId = null)
    {
        var list = new List<SessionSummary>();
        if (!File.Exists(paths.Sessions)) return list;
        lock (_sessionLock)
        {
            foreach (var line in File.ReadLines(paths.Sessions))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var s = JsonSerializer.Deserialize<SessionSummary>(line, Json.Compact);
                    if (s is not null && (gameId is null || s.GameId == gameId)) list.Add(s);
                }
                catch (JsonException) { /* skip a torn line */ }
            }
        }
        return list;
    }
}
