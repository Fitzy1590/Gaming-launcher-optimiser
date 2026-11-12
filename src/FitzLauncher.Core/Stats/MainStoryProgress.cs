using System.Globalization;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Stats;

/// <summary>Where a main-story percentage came from.</summary>
public enum MainStorySource { Finished, Entered, Playtime }

/// <summary>
/// How far through the main story a game is, for the progress ring. Achievements are deliberately left out:
/// they track side content and collectibles, not the story.
/// </summary>
public sealed record MainStoryProgress(double Percent, MainStorySource Source)
{
    /// <summary>An estimate from playtime never shows 100%: only marking the game Beaten or 100% does that.</summary>
    public const double EstimateCap = 99;

    public bool IsEstimate => Source == MainStorySource.Playtime;

    /// <summary>Whole-number percent for the centre of the ring, e.g. "42".</summary>
    public string RingText => Math.Round(Percent).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>"~42%" for an estimate, "42%" for an entered or finished value.</summary>
    public string PercentText => (IsEstimate ? "~" : "") + RingText + "%";

    /// <summary>
    /// Order: marked Beaten or 100% (story done), then the Done % typed in from the game's menu,
    /// then hours played against the main-story length. Null when none of these is known.
    /// </summary>
    public static MainStoryProgress? For(Game game)
    {
        if (game.Status is PlayStatus.Beaten or PlayStatus.Completed) return new(100, MainStorySource.Finished);
        if (game.CompletionPercent is { } c) return new(Math.Clamp(c, 0, 100), MainStorySource.Entered);
        if (game.MainStoryHours is > 0 and var m)
            return new(Math.Min(EstimateCap, Math.Max(0, game.TotalPlayTime.TotalHours) / m * 100), MainStorySource.Playtime);
        return null;
    }

    /// <summary>One-line detail under the ring, e.g. "8.4 h played of ~20 h · estimate from playtime".</summary>
    public static string Detail(Game game, MainStoryProgress? p)
    {
        var played = LibraryStats.FormatHours(game.TotalPlayTime.TotalHours) + " played";
        return p?.Source switch
        {
            MainStorySource.Playtime => $"{played} of ~{LibraryStats.FormatHours(game.MainStoryHours!.Value)} · estimate from playtime",
            MainStorySource.Entered => $"{played} · from the Done % you entered",
            MainStorySource.Finished => $"{played} · story finished",
            _ => $"{played}. Enter the main-story length or the Done % to see progress.",
        };
    }
}
