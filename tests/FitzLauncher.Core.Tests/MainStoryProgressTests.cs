using FitzLauncher.Core.Models;
using FitzLauncher.Core.Stats;

namespace FitzLauncher.Core.Tests;

public class MainStoryProgressTests
{
    private static Game G(double played = 0, double? story = null, int? done = null, PlayStatus status = PlayStatus.Playing,
        AchievementSnapshot? ach = null) => new()
        {
            Name = "Ashfall",
            TotalPlayTime = TimeSpan.FromHours(played),
            MainStoryHours = story,
            CompletionPercent = done,
            Status = status,
            Achievements = ach,
        };

    [Fact]
    public void Nothing_known_gives_no_progress()
    {
        Assert.Null(MainStoryProgress.For(G(played: 5)));
        Assert.Equal("5 h played. Enter the main-story length or the Done % to see progress.",
            MainStoryProgress.Detail(G(played: 5), null));
    }

    [Fact]
    public void Playtime_against_story_length_is_an_estimate()
    {
        var g = G(played: 8.4, story: 20);
        var p = MainStoryProgress.For(g)!;
        Assert.Equal(42, p.Percent, 3);
        Assert.True(p.IsEstimate);
        Assert.Equal("42", p.RingText);
        Assert.Equal("~42%", p.PercentText);
        Assert.Equal("8.4 h played of ~20 h · estimate from playtime", MainStoryProgress.Detail(g, p));
    }

    [Fact]
    public void Estimate_stops_at_99_until_marked_finished()
    {
        var p = MainStoryProgress.For(G(played: 45, story: 20))!;
        Assert.Equal(MainStoryProgress.EstimateCap, p.Percent);
        Assert.Equal("~99%", p.PercentText);

        var near = MainStoryProgress.For(G(played: 19.95, story: 20))!;
        Assert.Equal("99", near.RingText); // 99.75 is capped first, so it never rounds up to 100
    }

    [Theory]
    [InlineData(PlayStatus.Beaten)]
    [InlineData(PlayStatus.Completed)]
    public void Beaten_or_100_percent_is_finished_even_with_a_lower_entered_value(PlayStatus status)
    {
        var p = MainStoryProgress.For(G(played: 3, story: 20, done: 60, status: status))!;
        Assert.Equal(100, p.Percent);
        Assert.Equal(MainStorySource.Finished, p.Source);
        Assert.Equal("100%", p.PercentText);
    }

    [Fact]
    public void Entered_done_percent_beats_playtime_and_is_exact()
    {
        var g = G(played: 2, story: 20, done: 64);
        var p = MainStoryProgress.For(g)!;
        Assert.Equal(64, p.Percent);
        Assert.False(p.IsEstimate);
        Assert.Equal("64%", p.PercentText);
        Assert.Equal("2 h played · from the Done % you entered", MainStoryProgress.Detail(g, p));
    }

    [Fact]
    public void Achievements_are_ignored()
    {
        var ach = new AchievementSnapshot { Earned = 31, Total = 60 };
        Assert.Null(MainStoryProgress.For(G(ach: ach)));
        Assert.Equal(42, MainStoryProgress.For(G(played: 8.4, story: 20, ach: ach))!.Percent, 3);
    }

    [Fact]
    public void Unplayed_game_with_a_story_length_shows_zero()
    {
        var p = MainStoryProgress.For(G(story: 30))!;
        Assert.Equal(0, p.Percent);
        Assert.Equal("~0%", p.PercentText);
    }
}
