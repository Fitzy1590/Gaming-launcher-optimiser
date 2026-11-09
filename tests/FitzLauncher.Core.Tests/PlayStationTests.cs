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
