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
