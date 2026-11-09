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

public class TrainerOptionsTests
{
    private static List<FitzLauncher.Core.Models.TrainerCheat> L(params (string key, string name, bool on, bool manual)[] items) =>
        [.. items.Select(i => new FitzLauncher.Core.Models.TrainerCheat { Hotkey = i.key, Name = i.name, OnAtLaunch = i.on, Manual = i.manual })];

    [Fact]
    public void RefreshKeepsTicksAndHandAddedOptions()
    {
        var old = L(("Num 2", "Infinite Stamina", true, false), ("Num 9", "Mine", true, true), ("Num 5", "Infinite Oxygen", false, false));
        var fresh = L(("Num 2", "Infinite Stamina", false, false), ("Num 3", "Infinite Adrenaline", false, false), ("Num 5", "Infinite Oxygen (new name)", false, false));
        var merged = FitzLauncher.Core.Trainers.TrainerOptions.Merge(old, fresh);
        Assert.True(merged.Single(c => c.Hotkey == "Num 2").OnAtLaunch);                 // tick kept
        Assert.False(merged.Single(c => c.Hotkey == "Num 3").OnAtLaunch);                // new option: not ticked
        Assert.Equal("Infinite Oxygen (new name)", merged.Single(c => c.Hotkey == "Num 5").Name); // reworded: matched by hotkey
        Assert.Contains(merged, c => c.Name == "Mine" && c.Manual && c.OnAtLaunch);       // hand-added one survives
    }

    [Fact]
    public void ManualOptionsAreValidated()
    {
        var list = new List<FitzLauncher.Core.Models.TrainerCheat>();
        Assert.Null(FitzLauncher.Core.Trainers.TrainerOptions.AddManual(list, "  Ctrl+Num   5 ", "Zero Weight"));
        Assert.Equal("Ctrl+Num 5", list.Single().Hotkey);
        Assert.True(list.Single().OnAtLaunch && list.Single().Manual);
        Assert.NotNull(FitzLauncher.Core.Trainers.TrainerOptions.AddManual(list, "Ctrl+Num 5", "Zero Weight"));   // duplicate
        Assert.NotNull(FitzLauncher.Core.Trainers.TrainerOptions.AddManual(list, "Ctrl+Shift+Home", "Master"));   // master switch
        Assert.NotNull(FitzLauncher.Core.Trainers.TrainerOptions.AddManual(list, "Banana", "Nothing"));            // not a hotkey
        Assert.NotNull(FitzLauncher.Core.Trainers.TrainerOptions.AddManual(list, "Num 1", " "));                   // no name
        Assert.Single(list);
    }
}
