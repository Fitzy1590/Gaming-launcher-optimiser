using System.Globalization;
using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Stats;

/// <summary>Plain-English summary lines for a game: the library row and the details header.</summary>
public static class GameSummaryText
{
    /// <summary>"Backlog", "Playing", "Beaten", "100%", "Dropped", or "Not played yet" when nothing is set.</summary>
    public static string StatusLabel(PlayStatus status) => status switch
    {
        PlayStatus.Backlog => "Backlog",
        PlayStatus.Playing => "Playing",
        PlayStatus.Beaten => "Beaten",
        PlayStatus.Completed => "100%",
        PlayStatus.Abandoned => "Dropped",
        _ => "Not played yet",
    };

    /// <summary>"20.2 h", "35 min", or "" when there is under a minute of play.</summary>
    public static string Hours(TimeSpan played) =>
        played.TotalMinutes < 1 ? ""
        : played.TotalHours >= 1 ? $"{played.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h"
        : $"{played.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} min";

    /// <summary>Second line of a library row: "20.2 h · last played 12 Nov", or the status when never played.</summary>
    public static string RowLine(PlayStatus status, TimeSpan played, DateTimeOffset? lastPlayed)
    {
        var hours = Hours(played);
        if (hours.Length == 0) return StatusLabel(status);
        return lastPlayed is { } lp ? $"{hours} · last played {Date(lp)}" : hours;
    }

    /// <summary>Facts under the details title: "20.2 h played · Last played 12 Nov · 103 FPS average". Missing facts are left out.</summary>
    public static string Facts(TimeSpan played, DateTimeOffset? lastPlayed, double? averageFps)
    {
        var parts = new List<string>();
        var hours = Hours(played);
        parts.Add(hours.Length == 0 ? "Not played yet" : $"{hours} played");
        if (lastPlayed is { } lp) parts.Add($"Last played {Date(lp)}");
        if (averageFps is { } fps and > 0) parts.Add($"{fps.ToString("0", CultureInfo.InvariantCulture)} FPS average");
        return string.Join(" · ", parts);
    }

    private static string Date(DateTimeOffset d) => d.ToString("d MMM", CultureInfo.GetCultureInfo("en-GB"));
}
