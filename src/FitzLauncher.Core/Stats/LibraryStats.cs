using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Stats;

public enum LibrarySort { RecentlyPlayed, MostPlayed, Name, RecentlyAdded, Progress }

public sealed record LibraryTotals(double TotalHours, double WeekHours, double MonthHours, int Games, int Completed, int Playing, int Backlog);

public static class LibraryStats
{
    public static LibraryTotals Compute(IReadOnlyCollection<Game> games, IEnumerable<SessionSummary> sessions, DateTimeOffset now)
    {
        var list = sessions.ToList();
        double Hours(TimeSpan window) => list.Where(s => s.End >= now - window).Sum(s => s.Duration.TotalHours);
        return new LibraryTotals(
            games.Sum(g => g.TotalPlayTime.TotalHours),
            Hours(TimeSpan.FromDays(7)),
            Hours(TimeSpan.FromDays(30)),
            games.Count(g => !g.Hidden),
            games.Count(g => g.Status is PlayStatus.Completed or PlayStatus.Beaten),
            games.Count(g => g.Status == PlayStatus.Playing),
            games.Count(g => g.Status == PlayStatus.Backlog));
    }

    public static IEnumerable<Game> Sort(IEnumerable<Game> games, LibrarySort sort) =>
        games.Order(Comparer<Game>.Create((a, b) => Compare(a, b, sort)));

    /// <summary>Consistent ordering (ties broken by name) for list views.</summary>
    public static int Compare(Game a, Game b, LibrarySort sort)
    {
        var c = sort switch
        {
            LibrarySort.MostPlayed => b.TotalPlayTime.CompareTo(a.TotalPlayTime),
            LibrarySort.Name => 0,
            LibrarySort.RecentlyAdded => b.AddedAt.CompareTo(a.AddedAt),
            LibrarySort.Progress => (b.ProgressPercent ?? -1).CompareTo(a.ProgressPercent ?? -1),
            _ => (b.LastPlayed ?? b.AddedAt).CompareTo(a.LastPlayed ?? a.AddedAt),
        };
        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatHours(double hours) =>
        hours < 1 ? $"{hours * 60:0} min" : hours < 10 ? $"{hours:0.#} h" : $"{hours:0} h";
}
