using FitzLauncher.Core.Input;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class KeyStepsTests
{
    [Theory]
    [InlineData("Num 1", new byte[] { }, Vk.Num0 + 1)]
    [InlineData("Num 0", new byte[] { }, Vk.Num0)]
    [InlineData("Ctrl+Num 1", new byte[] { Vk.Ctrl }, Vk.Num0 + 1)]
    [InlineData("Alt+Num /", new byte[] { Vk.Alt }, Vk.NumDivide)]
    [InlineData("Alt+Num *", new byte[] { Vk.Alt }, Vk.NumMultiply)]
    [InlineData("Alt+Num -", new byte[] { Vk.Alt }, Vk.NumSubtract)]
    [InlineData("Alt+Num +", new byte[] { Vk.Alt }, Vk.NumAdd)]
    [InlineData("Ctrl + Shift + Home", new byte[] { Vk.Ctrl, Vk.Shift }, Vk.Home)]
    [InlineData("F5", new byte[] { }, Vk.F1 + 4)]
    [InlineData("Numpad 7", new byte[] { }, Vk.Num0 + 7)]
    [InlineData("Ctrl+K", new byte[] { Vk.Ctrl }, (byte)'K')]
    public void ParsesTrainerHotkeys(string text, byte[] mods, int key)
    {
        var c = HotkeyParser.Parse(text)!;
        Assert.Equal(mods, c.Modifiers.ToArray());
        Assert.Equal((byte)key, c.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Something odd")]
    [InlineData("Ctrl+")]
    public void RejectsWhatItCannotSend(string text) => Assert.Null(HotkeyParser.Parse(text));

    [Fact]
    public void ChordPressesModifiersAroundTheKeyAndReleasesInReverse()
    {
        var steps = HotkeyParser.Parse("Ctrl+Shift+Num 2")!.Steps();
        Assert.Equal(
            [new KeyStep(Vk.Ctrl, true), new KeyStep(Vk.Shift, true), new KeyStep(Vk.Num0 + 2, true), new KeyStep(Vk.Num0 + 2, false), new KeyStep(Vk.Shift, false), new KeyStep(Vk.Ctrl, false)],
            steps);
    }

    [Fact]
    public void NeverSendsTheTrainerMasterSwitch()
    {
        Assert.True(HotkeyParser.IsTrainerMasterSwitch(HotkeyParser.Parse("Ctrl+Shift+Home")!));
        Assert.False(HotkeyParser.IsTrainerMasterSwitch(HotkeyParser.Parse("Ctrl+Num 1")!));
    }

    [Fact]
    public void AltTabHoldsAltAcrossPressesAndReleasesWhenPsIsLetGo()
    {
        var a = new AltTabHold();
        Assert.Equal([new KeyStep(Vk.Alt, true), new KeyStep(Vk.Tab, true), new KeyStep(Vk.Tab, false)], a.Press());   // first press: Alt down + Tab
        Assert.Equal([new KeyStep(Vk.Tab, true), new KeyStep(Vk.Tab, false)], a.Press());                                // next: just Tab (cycles)
        Assert.Equal([new KeyStep(Vk.Shift, true), new KeyStep(Vk.Tab, true), new KeyStep(Vk.Tab, false), new KeyStep(Vk.Shift, false)], a.Press(back: true));
        Assert.True(a.Held);
        Assert.Equal([new KeyStep(Vk.Alt, false)], a.Release());                                                          // PS released: Alt up picks the window
        Assert.False(a.Held);
        Assert.Empty(a.Release());                                                                                        // nothing left to release
        Assert.Equal(3, a.Press().Count);                                                                                 // a fresh hold starts again with Alt down
    }
}
