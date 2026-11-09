using FitzLauncher.Core.Input;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class PlayStationTests
{
    private static byte[] Report(int length, params (int index, byte value)[] set)
    {
        var r = new byte[length];
        foreach (var (i, v) in set) r[i] = v;
        return r;
    }

    [Fact]
    public void Ds4UsbCrossAndDpadUp()
    {
        // id 0x01, sticks centred, hat 0 (up) + Cross (0x20)
        var r = Report(64, (0, 0x01), (1, 128), (2, 128), (3, 128), (4, 128), (5, 0x20), (8, 200));
        var s = PlayStationReport.Parse(r, PlayStationModel.DualShock4, bluetooth: false)!.Value;
        Assert.True(s.Has(PadSnapshot.A));
        Assert.True(s.Has(PadSnapshot.Up));
        Assert.False(s.Has(PadSnapshot.Down));
        Assert.Equal(200, s.LT);
        Assert.Equal(0, s.LX);
    }

    [Fact]
    public void Ds4HatReleasedHasNoDirections()
    {
        var r = Report(64, (0, 0x01), (5, 0x08));
        var s = PlayStationReport.Parse(r, PlayStationModel.DualShock4, false)!.Value;
        Assert.Equal(0, s.Buttons & 0x000F);
    }

    [Fact]
    public void Ds4BluetoothFullReportAndPsButton()
    {
        // id 0x11: data shifted by two; PS at byte 9
        var r = Report(78, (0, 0x11), (3, 128), (4, 0), (5, 128), (6, 128), (7, 0x08 | 0x40), (9, 0x01));
        var s = PlayStationReport.Parse(r, PlayStationModel.DualShock4, true)!.Value;
        Assert.True(s.Has(PadSnapshot.Guide));
        Assert.True(s.Has(PadSnapshot.B)); // Circle
        Assert.True(s.LY > 30000);          // stick pushed up
    }

    [Fact]
    public void DualSenseUsbLayout()
    {
        // sticks 1-4, L2 5, R2 6, hat/face 8, buttons 9, PS 10
        var r = Report(64, (0, 0x01), (1, 255), (2, 128), (3, 128), (4, 128), (6, 255), (8, 0x02 | 0x80), (9, 0x20 | 0x02), (10, 0x01));
        var s = PlayStationReport.Parse(r, PlayStationModel.DualSense, false)!.Value;
        Assert.True(s.Has(PadSnapshot.Right));
        Assert.True(s.Has(PadSnapshot.Y));      // Triangle
        Assert.True(s.Has(PadSnapshot.Start));  // Options
        Assert.True(s.Has(PadSnapshot.RightShoulder));
        Assert.True(s.Has(PadSnapshot.Guide));
        Assert.Equal(255, s.RT);
        Assert.True(s.LX > 30000);
    }

    [Fact]
    public void DualSenseBluetoothReports()
    {
        var full = Report(78, (0, 0x31), (9, 0x04 | 0x20), (11, 0x01));
        var s = PlayStationReport.Parse(full, PlayStationModel.DualSense, true)!.Value;
        Assert.True(s.Has(PadSnapshot.Down));
        Assert.True(s.Has(PadSnapshot.A));
        Assert.True(s.Has(PadSnapshot.Guide));

        // short Bluetooth report 0x01 uses the DS4 layout
        var simple = Report(78, (0, 0x01), (5, 0x06 | 0x10), (7, 0x01));
        var t = PlayStationReport.Parse(simple, PlayStationModel.DualSense, true)!.Value;
        Assert.True(t.Has(PadSnapshot.Left));
        Assert.True(t.Has(PadSnapshot.X));
        Assert.True(t.Has(PadSnapshot.Guide));
    }

    [Fact]
    public void UnknownReportsAndDevicesIgnored()
    {
        Assert.Null(PlayStationReport.Parse(Report(64, (0, 0x05)), PlayStationModel.DualShock4, false));
        Assert.Null(PlayStationReport.ModelFor(0x045E, 0x028E)); // Xbox 360 pad
        Assert.Equal(PlayStationModel.DualShock4, PlayStationReport.ModelFor(0x054C, 0x09CC));
        Assert.Equal(PlayStationModel.DualSense, PlayStationReport.ModelFor(0x054C, 0x0CE6));
    }
}

public class PadMergeTests
{
    [Fact]
    public void MergesButtonsAndTakesTheFurtherStick()
    {
        var builtIn = new PadSnapshot(PadSnapshot.A, 100, 0, 0, 0, 10, 0);
        var ds4 = new PadSnapshot(PadSnapshot.B, -20000, 5000, 0, 9000, 0, 200);
        var m = PadMerge.Combine(builtIn, ds4);
        Assert.True(m.Has(PadSnapshot.A) && m.Has(PadSnapshot.B));
        Assert.Equal(-20000, m.LX);
        Assert.Equal(5000, m.LY);
        Assert.Equal(9000, m.RY);
        Assert.Equal(10, m.LT);
        Assert.Equal(200, m.RT);
    }

    [Fact]
    public void EitherMissingPassesTheOtherThrough()
    {
        var p = new PadSnapshot(PadSnapshot.X, 1, 2, 3, 4, 5, 6);
        Assert.Equal(p, PadMerge.Combine(null, p));
        Assert.Equal(p, PadMerge.Combine(p, null));
        Assert.Equal(default, PadMerge.Combine(null, null));
    }
}

public class PadComboTests
{
    private static PadSnapshot P(ushort buttons) => new(buttons, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void PsPlusUpRaisesVolumeAndRepeatsWhileHeld()
    {
        var e = new PadComboEngine(() => new PadComboSettings());
        var t = new DateTime(2026, 1, 1);
        Assert.Empty(e.Update(P(PadSnapshot.Guide), t));
        Assert.True(e.Chording);
        Assert.Equal([PadAction.VolumeUp], e.Update(P(PadSnapshot.Guide | PadSnapshot.Up), t = t.AddMilliseconds(50)));
        Assert.Empty(e.Update(P(PadSnapshot.Guide | PadSnapshot.Up), t = t.AddMilliseconds(200)));
        Assert.Equal([PadAction.VolumeUp], e.Update(P(PadSnapshot.Guide | PadSnapshot.Up), t = t.AddMilliseconds(300))); // repeat
        Assert.Equal([PadAction.VolumeDown], e.Update(P(PadSnapshot.Guide | PadSnapshot.Down), t = t.AddMilliseconds(20)));
        Assert.Empty(e.Update(P(0), t.AddMilliseconds(20))); // release after a combo: no tap
    }

    [Fact]
    public void QuickPsTapOnItsOwnIsATap()
    {
        var e = new PadComboEngine(() => new PadComboSettings());
        var t = DateTime.UtcNow;
        e.Update(P(PadSnapshot.Guide), t);
        Assert.Equal([PadComboEngine.Tap], e.Update(P(0), t.AddMilliseconds(150)));
        // A long hold isn't a tap.
        e.Update(P(PadSnapshot.Guide), t = t.AddSeconds(1));
        Assert.Empty(e.Update(P(0), t.AddSeconds(2)));
    }

    [Fact]
    public void NonRepeatingActionsFireOnceAndDisabledFiresNothing()
    {
        var e = new PadComboEngine(() => new PadComboSettings());
        var t = DateTime.UtcNow;
        e.Update(P(PadSnapshot.Guide), t);
        Assert.Equal([PadAction.Screenshot], e.Update(P(PadSnapshot.Guide | PadSnapshot.Back), t = t.AddMilliseconds(30)));
        Assert.Empty(e.Update(P(PadSnapshot.Guide | PadSnapshot.Back), t.AddSeconds(2)));

        var off = new PadComboEngine(() => new PadComboSettings { Enabled = false });
        off.Update(P(PadSnapshot.Guide), t);
        Assert.Empty(off.Update(P(PadSnapshot.Guide | PadSnapshot.Up), t.AddMilliseconds(30)));
        Assert.Empty(off.Update(P(0), t.AddMilliseconds(60))); // pressing a button still cancels the tap
    }
}

public class PadComboDefaultsTests
{
    [Fact]
    public void NewDefaultsSleepOnLeftAndHideCountersOnRight()
    {
        var s = PadComboSettings.New();
        Assert.Equal(PadAction.Sleep, s.ActionFor(PadSnapshot.Left));
        Assert.Equal(PadAction.ToggleOverlay, s.ActionFor(PadSnapshot.Right));
        Assert.Equal(PadAction.BrightnessUp, s.ActionFor(PadSnapshot.RightShoulder));
    }

    [Fact]
    public void OldUntouchedDefaultsMigrateButCustomOnesStay()
    {
        var old = new PadComboSettings
        {
            Combos =
            [
                new() { Button = PadSnapshot.Left, Action = PadAction.BrightnessDown }, new() { Button = PadSnapshot.Right, Action = PadAction.BrightnessUp },
                new() { Button = PadSnapshot.LeftShoulder, Action = PadAction.PreviousTrack }, new() { Button = PadSnapshot.RightShoulder, Action = PadAction.NextTrack },
            ],
        };
        old.AddNewDefaults();
        Assert.Equal(PadAction.Sleep, old.ActionFor(PadSnapshot.Left));
        Assert.Equal(PadAction.TrainerOrGame, old.ActionFor(PadSnapshot.RightThumb));

        var custom = new PadComboSettings { Combos = [new() { Button = PadSnapshot.Left, Action = PadAction.Mute }] };
        custom.AddNewDefaults();
        Assert.Equal(PadAction.Mute, custom.ActionFor(PadSnapshot.Left));
        Assert.Equal(PadComboSettings.CurrentDefaults, custom.DefaultsVersion);
    }
}

public class GameHelpComboTests
{
    [Fact]
    public void TriangleOpensGameHelpAndKeyboardMovesToL3()
    {
        Assert.Equal(PadAction.GameHelp, PadComboSettings.New().ActionFor(PadSnapshot.Y));
        var v2 = new PadComboSettings { DefaultsVersion = 2, Combos = [new() { Button = PadSnapshot.Y, Action = PadAction.OnScreenKeyboard }] };
        v2.AddNewDefaults();
        Assert.Equal(PadAction.GameHelp, v2.ActionFor(PadSnapshot.Y));
        Assert.Equal(PadAction.OnScreenKeyboard, v2.ActionFor(PadSnapshot.LeftThumb));
    }
}

public class ActionsWheelTests
{
    private static PadSnapshot P(ushort buttons, short rx = 0, short ry = 0) => new(buttons, 0, 0, rx, ry, 0, 0);
    private static readonly DateTime T0 = new(2026, 1, 1);

    [Theory]
    [InlineData(0, 30000, 0)]      // up → top
    [InlineData(22000, 22000, 1)]  // up-right
    [InlineData(30000, 0, 2)]      // right
    [InlineData(0, -30000, 4)]     // down
    [InlineData(-30000, 0, 6)]     // left
    [InlineData(-22000, 22000, 7)] // up-left
    public void RightStickDirectionPicksTheSlot(int rx, int ry, int slot) =>
        Assert.Equal(slot, PadComboSettings.SlotFor((short)rx, (short)ry));

    [Fact]
    public void SlotSelectionHasDeadzoneAndHysteresis()
    {
        Assert.Equal(-1, PadComboSettings.SlotFor(2000, 3000));        // centred
        Assert.Equal(-1, PadComboSettings.SlotFor(0, 12000));          // not far enough to select
        Assert.Equal(0, PadComboSettings.SlotFor(0, 12000, current: 0)); // …but keeps an existing selection
        Assert.Equal(-1, PadComboSettings.SlotFor(0, 3000, current: 0)); // released back to centre
    }

    [Fact]
    public void HoldingPsOpensTheWheelAndReleasingRunsTheHighlightedSlot()
    {
        var e = new PadComboEngine(() => PadComboSettings.New());
        e.Update(P(PadSnapshot.Guide), T0);
        Assert.False(e.Wheel.Open);                                   // too soon
        e.Update(P(PadSnapshot.Guide), T0.AddMilliseconds(400));
        Assert.True(e.Wheel.Open);
        e.Update(P(PadSnapshot.Guide, 0, 30000), T0.AddMilliseconds(500));
        Assert.Equal(new WheelState(true, 0), e.Wheel);
        Assert.Equal([PadAction.GameHelp], e.Update(P(0), T0.AddMilliseconds(600))); // default top slot; no OneXConsole tap
        Assert.False(e.Wheel.Open);
    }

    [Fact]
    public void ReleasingWithTheStickCentredCancelsAndNeverActsAsATap()
    {
        var e = new PadComboEngine(() => PadComboSettings.New());
        e.Update(P(PadSnapshot.Guide), T0);
        e.Update(P(PadSnapshot.Guide), T0.AddMilliseconds(500));
        Assert.Empty(e.Update(P(0), T0.AddMilliseconds(700)));
    }

    [Fact]
    public void ShortPressIsStillATapAndAShortcutButtonClosesTheWheel()
    {
        var e = new PadComboEngine(() => PadComboSettings.New());
        e.Update(P(PadSnapshot.Guide), T0);
        Assert.Equal([PadComboEngine.Tap], e.Update(P(0), T0.AddMilliseconds(200)));

        e.Update(P(PadSnapshot.Guide), T0.AddSeconds(5));
        e.Update(P(PadSnapshot.Guide), T0.AddSeconds(6));
        Assert.True(e.Wheel.Open);
        Assert.Equal([PadAction.VolumeUp], e.Update(P(PadSnapshot.Guide | PadSnapshot.Up), T0.AddSeconds(6.1)));
        Assert.False(e.Wheel.Open);
    }

    [Fact]
    public void WheelCanBeSwitchedOff()
    {
        var cfg = PadComboSettings.New();
        cfg.WheelEnabled = false;
        var e = new PadComboEngine(() => cfg);
        e.Update(P(PadSnapshot.Guide), T0);
        e.Update(P(PadSnapshot.Guide), T0.AddSeconds(2));
        Assert.False(e.Wheel.Open);
    }

    [Fact]
    public void TheGameKeepsButtonsHeldBeforePsAndNeverSeesPsOrNewPresses()
    {
        var e = new PadComboEngine(() => PadComboSettings.New());
        const ushort block = PadSnapshot.LeftShoulder; // held before PS goes down
        var held = P(block);
        Assert.Equal(block, e.GameView(held).Buttons);                      // before PS: untouched

        e.Update(P((ushort)(block | PadSnapshot.Guide)), T0);
        Assert.Equal(block, e.GameView(P((ushort)(block | PadSnapshot.Guide))).Buttons); // still held, PS hidden

        var withUp = P((ushort)(block | PadSnapshot.Guide | PadSnapshot.Up));
        e.Update(withUp, T0.AddMilliseconds(50));
        Assert.Equal(block, e.GameView(withUp).Buttons);                   // the new Up press is the shortcut's, not the game's

        e.Update(P(PadSnapshot.Guide), T0.AddMilliseconds(100));            // block released
        Assert.Equal(0, e.GameView(P(PadSnapshot.Guide)).Buttons);
        e.Update(P(0), T0.AddMilliseconds(150));
        Assert.Equal(PadSnapshot.Up, e.GameView(P(PadSnapshot.Up)).Buttons); // PS up: everything passes again
    }

    [Fact]
    public void TheRightStickBelongsToTheWheelWhileItIsOpen()
    {
        var e = new PadComboEngine(() => PadComboSettings.New());
        var s = new PadSnapshot(PadSnapshot.Guide, 100, 200, 5000, 6000, 40, 50);
        e.Update(s, T0);
        Assert.Equal(5000, e.GameView(s).RX);                                // before the wheel opens: passes through
        e.Update(s, T0.AddMilliseconds(500));
        var view = e.GameView(s);
        Assert.Equal((short)0, view.RX);
        Assert.Equal((short)0, view.RY);
        Assert.Equal((short)100, view.LX);                                   // left stick and triggers still pass
        Assert.Equal((byte)50, view.RT);
    }

    [Fact]
    public void SavedSettingsGetTheDefaultWheel()
    {
        var old = new PadComboSettings { DefaultsVersion = 3, WheelActions = [] };
        old.AddNewDefaults();
        Assert.Equal(PadComboSettings.WheelSlots, old.WheelActions.Count);
        Assert.Equal(PadComboSettings.CurrentDefaults, old.DefaultsVersion);
    }
}
