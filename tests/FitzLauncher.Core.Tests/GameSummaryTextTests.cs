using FitzLauncher.Core.Models;
using FitzLauncher.Core.Stats;

namespace FitzLauncher.Core.Tests;

public class GameSummaryTextTests
{
    private static readonly DateTimeOffset Nov12 = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RowLine_unplayed_shows_status() =>
        Assert.Equal("Backlog", GameSummaryText.RowLine(PlayStatus.Backlog, TimeSpan.Zero, null));

    [Fact]
    public void RowLine_unplayed_with_no_status() =>
        Assert.Equal("Not played yet", GameSummaryText.RowLine(PlayStatus.None, TimeSpan.FromSeconds(20), null));

    [Fact]
    public void RowLine_played_shows_hours_and_date() =>
        Assert.Equal("20.2 h · last played 12 Nov", GameSummaryText.RowLine(PlayStatus.Playing, TimeSpan.FromHours(20.2), Nov12));

    [Fact]
    public void RowLine_short_play_uses_minutes() =>
        Assert.Equal("35 min", GameSummaryText.RowLine(PlayStatus.Playing, TimeSpan.FromMinutes(35), null));

    [Fact]
    public void Facts_include_everything_known() =>
        Assert.Equal("20.2 h played · Last played 12 Nov · 103 FPS average", GameSummaryText.Facts(TimeSpan.FromHours(20.2), Nov12, 103.4));

    [Fact]
    public void Facts_leave_out_what_is_missing() =>
        Assert.Equal("Not played yet", GameSummaryText.Facts(TimeSpan.Zero, null, null));
}
