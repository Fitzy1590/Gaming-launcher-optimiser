namespace FitzLauncher.Core.Emulation;

/// <summary>How one game has run on one emulator, over all sessions (time-weighted).</summary>
public sealed class EmuPerfRecord
{
    public int Sessions { get; set; }
    public double Seconds { get; set; }
    public double AvgFps { get; set; }
    public double OnePercentLow { get; set; }
    public double TimeAtTarget { get; set; }
    public int? LastLevel { get; set; }
    public DateTimeOffset LastPlayed { get; set; }
}

public enum SuggestionKind { Measured, Successor, Trial }

public sealed record EmulatorSuggestion(SuggestionKind Kind, string FromId, string ToId, string Message);

public static class EmulatorAdvisor
{
    public const double MinSeconds = 600;
    public const int MinSessions = 2;

    /// <summary>Adds one finished session to the game's record for that emulator (sessions under 2 minutes or without FPS data are ignored).</summary>
    public static bool Record(Dictionary<string, EmuPerfRecord> perf, string emulatorId, double seconds, bool hasFps, double avg, double low, double? atTarget, int? level, DateTimeOffset when)
    {
        if (!hasFps || seconds < 120) return false;
        if (!perf.TryGetValue(emulatorId, out var r)) perf[emulatorId] = r = new EmuPerfRecord();
        var total = r.Seconds + seconds;
        r.AvgFps = (r.AvgFps * r.Seconds + avg * seconds) / total;
        r.OnePercentLow = (r.OnePercentLow * r.Seconds + low * seconds) / total;
        r.TimeAtTarget = (r.TimeAtTarget * r.Seconds + (atTarget ?? 0) * seconds) / total;
        r.Seconds = total;
        r.Sessions++;
        r.LastLevel = level ?? r.LastLevel;
        r.LastPlayed = when;
        return true;
    }

    public static double Score(EmuPerfRecord r, int target) =>
        target <= 0 ? 0 : 0.45 * Math.Min(r.AvgFps / target, 1.2) + 0.45 * Math.Min(r.OnePercentLow / target, 1.2) + 0.10 * r.TimeAtTarget;

    private static bool Enough(EmuPerfRecord r) => r.Sessions >= MinSessions && r.Seconds >= MinSeconds;

    /// <summary>
    /// A better emulator for this game, only when it is established: (1) your own sessions show another emulator clearly
    /// ahead, (2) the current emulator is no longer updated and has a successor, (3) the game misses its target even at the
    /// lowest quality and another installed emulator hasn't been tried (a trial, labelled as such).
    /// </summary>
    public static EmulatorSuggestion? Suggest(EmuPlatform platform, string currentId, int target, IReadOnlyDictionary<string, EmuPerfRecord> perf,
        IReadOnlyList<EmulatorSource> sources, ISet<string> installedIds, int currentLevel)
    {
        string Name(string id) => sources.FirstOrDefault(s => s.Id == id)?.Name ?? id;
        var candidates = Platforms.Get(platform).EmulatorIds.Where(id => id != currentId).ToList();

        // 1. Measured.
        if (perf.TryGetValue(currentId, out var cur) && Enough(cur))
        {
            var curScore = Score(cur, target);
            var best = candidates.Where(id => perf.TryGetValue(id, out var o) && Enough(o))
                .Select(id => (id, rec: perf[id], score: Score(perf[id], target))).OrderByDescending(x => x.score).FirstOrDefault();
            if (best.id is not null && best.score >= curScore * 1.15 && best.score - curScore >= 0.08)
                return new(SuggestionKind.Measured, currentId, best.id,
                    $"On this game {Name(best.id)} averaged {best.rec.AvgFps:0} FPS (1% low {best.rec.OnePercentLow:0}) against {cur.AvgFps:0} ({cur.OnePercentLow:0}) on {Name(currentId)}. Switch to {Name(best.id)}?");
        }

        // 2. The emulator has been replaced by a maintained one.
        var src = sources.FirstOrDefault(s => s.Id == currentId);
        if (src?.SupersededBy is { } successor && candidates.Contains(successor) && installedIds.Contains(successor))
            return new(SuggestionKind.Successor, currentId, successor, $"{src.SupersededReason ?? $"{Name(currentId)} is no longer updated."} Switch this game to {Name(successor)}?");

        // 3. Struggling at the floor with an untried alternative.
        if (perf.TryGetValue(currentId, out var cur2) && Enough(cur2) && currentLevel <= QualityLevel.Min && Score(cur2, target) < 0.8)
        {
            var untried = candidates.FirstOrDefault(id => installedIds.Contains(id) && !perf.ContainsKey(id));
            if (untried is not null)
                return new(SuggestionKind.Trial, currentId, untried,
                    $"This game misses {target} FPS even at the lowest quality on {Name(currentId)}. {Name(untried)} also runs {Platforms.Get(platform).Name} and is installed. It isn't proven better, but a session or two on it will tell, and Fitz will compare them.");
        }
        return null;
    }
}
