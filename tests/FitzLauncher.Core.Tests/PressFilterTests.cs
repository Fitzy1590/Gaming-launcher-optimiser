using FitzLauncher.Core.Input;

namespace FitzLauncher.Core.Tests;

public class PressFilterTests
{
    private enum B { Up, Down, Left, Right, A, RightShoulder }

    private static PressFilter<B> Make() => new(
        b => b is B.Up or B.Down or B.Left or B.Right,
        b => b is B.Up or B.Down or B.Left or B.Right);

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime T(int ms) => T0.AddMilliseconds(ms);
    private static B[] Down(params B[] b) => b;

    [Fact]
    public void One_press_fires_once_however_long_it_is_held_for_non_repeating_buttons()
    {
        var f = Make();
        Assert.Equal([B.RightShoulder], f.Step(Down(B.RightShoulder), T(0)));
        for (var ms = 16; ms < 2000; ms += 16) Assert.Empty(f.Step(Down(B.RightShoulder), T(ms)));
    }

    [Fact]
    public void A_short_gap_between_two_routes_reporting_the_same_press_is_not_a_second_press()
    {
        var f = Make();
        Assert.Single(f.Step(Down(B.RightShoulder), T(0)));          // XInput sees it
        Assert.Empty(f.Step(Down(), T(16)));                          // it lets go...
        Assert.Empty(f.Step(Down(), T(48)));                          // ...before the HID route reports it
        Assert.Empty(f.Step(Down(B.RightShoulder), T(64)));           // second route: same press
        Assert.Empty(f.Step(Down(B.RightShoulder), T(96)));
    }

    [Fact]
    public void Contact_chatter_inside_the_grace_period_does_not_double_press()
    {
        var f = Make();
        Assert.Single(f.Step(Down(B.A), T(0)));
        Assert.Empty(f.Step(Down(), T(16)));
        Assert.Empty(f.Step(Down(B.A), T(32)));
        Assert.Empty(f.Step(Down(), T(48)));
        Assert.Empty(f.Step(Down(B.A), T(64)));
    }

    [Fact]
    public void A_real_second_tap_after_the_grace_period_counts()
    {
        var f = Make();
        Assert.Single(f.Step(Down(B.A), T(0)));
        Assert.Empty(f.Step(Down(), T(30)));
        Assert.Empty(f.Step(Down(), T(100)));                         // up for over 80 ms: released
        Assert.Equal([B.A], f.Step(Down(B.A), T(130)));
    }

    [Fact]
    public void Held_directions_repeat_after_a_delay_and_keep_repeating()
    {
        var f = Make();
        Assert.Equal([B.Down], f.Step(Down(B.Down), T(0)));
        Assert.Empty(f.Step(Down(B.Down), T(200)));
        Assert.Equal([B.Down], f.Step(Down(B.Down), T(400)));        // after 380 ms
        Assert.Empty(f.Step(Down(B.Down), T(450)));
        Assert.Equal([B.Down], f.Step(Down(B.Down), T(520)));        // then every 110 ms
    }

    [Fact]
    public void Rolling_the_thumb_through_a_diagonal_moves_one_step_not_two()
    {
        var f = Make();
        Assert.Equal([B.Down], f.Step(Down(B.Down), T(0)));
        Assert.Empty(f.Step(Down(B.Down, B.Right), T(16)));          // diagonal for a moment
        Assert.Empty(f.Step(Down(B.Right), T(32)));
        Assert.Empty(f.Step(Down(B.Right), T(48)));
    }

    [Fact]
    public void Two_deliberate_directions_a_moment_apart_both_count()
    {
        var f = Make();
        Assert.Equal([B.Down], f.Step(Down(B.Down), T(0)));
        Assert.Empty(f.Step(Down(), T(100)));
        Assert.Equal([B.Right], f.Step(Down(B.Right), T(200)));
    }

    [Fact]
    public void Different_buttons_pressed_together_each_fire_once()
    {
        var f = Make();
        var fired = f.Step(Down(B.A, B.RightShoulder), T(0));
        Assert.Equal(2, fired.Count);
        Assert.Empty(f.Step(Down(B.A, B.RightShoulder), T(16)));
    }
}
