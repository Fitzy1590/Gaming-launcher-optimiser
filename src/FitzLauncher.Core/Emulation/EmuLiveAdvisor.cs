using System.Globalization;
using System.Text.RegularExpressions;

namespace FitzLauncher.Core.Emulation;

public sealed record EmuAdvice(string Short, string Long);

/// <summary>
/// Watches a running emulator game: when the frame rate stays under the target for a while it says why (graphics card or processor)
/// and what to do. Short text for the overlay, full text for the end of the session. Each kind of advice appears once per session.
/// </summary>
public sealed class EmuLiveAdvisor(int target, bool autoTuneOn)
{
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan Sustained = TimeSpan.FromSeconds(20);

    private TimeSpan? _belowSince;
    private readonly HashSet<string> _given = [];

    public EmuAdvice? Evaluate(double fps, double onePercentLow, double? gpuLoad, TimeSpan elapsed)
    {
        if (target <= 0 || elapsed < Warmup || fps <= 0) { _belowSince = null; return null; }
        if (fps >= target * 0.9) { _belowSince = null; return null; }
        _belowSince ??= elapsed;
        if (elapsed - _belowSince < Sustained) return null;

        var kind = gpuLoad switch { >= 90 => "gpu", < 80 => "cpu", _ => "mixed" };
        if (!_given.Add(kind)) return null;
        var now = $"{fps:0} FPS against a {target} FPS target";
        return kind switch
        {
            "gpu" => new("Below target: graphics card is maxed out.",
                $"The game ran at {now} while the graphics card was flat out, so quality is too high for this PC. " +
                (autoTuneOn ? "Fitz will lower the quality a step for next time." : "Press Optimise on the game page to lower the quality for next time.")),
            "cpu" => new("Below target: the processor is the limit.",
                $"The game ran at {now} but the graphics card wasn't busy, so the processor (the emulation itself) is the limit. " +
                "Lowering the resolution won't help. Close other programs, and if it stays low try a lower FPS target or another emulator."),
            _ => new("Below target: both are close to their limit.",
                $"The game ran at {now} with the graphics card and processor both fairly busy. " +
                (autoTuneOn ? "Fitz will lower the quality a step for next time." : "Press Optimise on the game page to lower the quality for next time.")),
        };
    }
}

public enum TitleFileKind { Base, Update, Dlc }

public sealed record TitleFile(string Path, string TitleId, string BaseId, int? Version, TitleFileKind Kind);

/// <summary>Works out which update and DLC files in the user's own library belong to a Switch game, from the title IDs in the file names (no game data is read, so no keys are involved).</summary>
public static partial class TitleRelations
{
    [GeneratedRegex(@"\[(?<id>0100[0-9A-Fa-f]{12})\]", RegexOptions.None)]
    private static partial Regex Id();

    [GeneratedRegex(@"\[v(?<v>\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Ver();

    public static TitleFile? Parse(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (Id().Match(name) is not { Success: true } m) return null;
        var tid = m.Groups["id"].Value.ToUpperInvariant();
        var value = ulong.Parse(tid, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var low = value & 0xFFF;
        var high = value >> 12;
        int? version = Ver().Match(name) is { Success: true } v && int.TryParse(v.Groups["v"].Value, out var vi) ? vi : null;

        TitleFileKind kind;
        ulong baseValue;
        if (low == 0x800) { kind = TitleFileKind.Update; baseValue = value & ~0xFFFUL; }
        else if ((high & 1) == 1) { kind = TitleFileKind.Dlc; baseValue = (high - 1) << 12; }
        else { kind = TitleFileKind.Base; baseValue = value & ~0xFFFUL; }
        return new TitleFile(path, tid, baseValue.ToString("X16", CultureInfo.InvariantCulture), version, kind);
    }

    public sealed record Summary(string BaseId, int? NewestUpdate, int UpdateFiles, int DlcFiles)
    {
        public string Text
        {
            get
            {
                var parts = new List<string>();
                if (UpdateFiles > 0) parts.Add(NewestUpdate is { } v ? $"an update file (newest v{v})" : "an update file");
                if (DlcFiles > 0) parts.Add(DlcFiles == 1 ? "1 DLC file" : $"{DlcFiles} DLC files");
                return parts.Count == 0
                    ? "No update or DLC files for this game were found next to it. If you expect one, put it in the same folder or a folder beside it."
                    : $"Found in your library: {string.Join(" and ", parts)}. Make sure they're installed in the emulator (Fitz can't check that).";
            }
        }
    }

    public static Summary Summarise(string gameFile, IEnumerable<string> otherFiles)
    {
        var self = Parse(gameFile);
        if (self is null) return new Summary("", null, 0, 0);
        var related = otherFiles.Select(Parse).OfType<TitleFile>().Where(f => f.BaseId == self.BaseId && f.Kind != TitleFileKind.Base).ToList();
        var updates = related.Where(f => f.Kind == TitleFileKind.Update).ToList();
        return new Summary(self.BaseId, updates.Select(u => u.Version).Where(v => v is not null).DefaultIfEmpty(null).Max(), updates.Count, related.Count(f => f.Kind == TitleFileKind.Dlc));
    }
}
