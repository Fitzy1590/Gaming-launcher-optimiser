namespace FitzLauncher.Core.Input;

public enum PlayStationModel { DualShock4, DualSense }

/// <summary>
/// One controller reading in XInput terms: <see cref="Buttons"/> uses the XInput bit layout (D-pad 0x000F,
/// Start 0x0010, Back 0x0020, thumbs 0x0040/0x0080, shoulders 0x0100/0x0200, Guide 0x0400, A/B/X/Y 0x1000-0x8000),
/// sticks are -32768..32767 with up positive, triggers 0..255.
/// </summary>
public readonly record struct PadSnapshot(ushort Buttons, short LX, short LY, short RX, short RY, byte LT, byte RT)
{
    public const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008, Start = 0x0010, Back = 0x0020,
        LeftThumb = 0x0040, RightThumb = 0x0080, LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400,
        A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    public bool Has(ushort mask) => (Buttons & mask) != 0;
}

/// <summary>
/// Reads Sony controller input reports (DualShock 4 and DualSense, over USB or Bluetooth) into
/// <see cref="PadSnapshot"/>s, with Cross = A, Circle = B, Square = X, Triangle = Y, Options = Start,
/// Share/Create = Back and the PS button = Guide.
/// </summary>
public static class PlayStationReport
{
    public const ushort SonyVendorId = 0x054C;

    /// <summary>Known product ids: DS4 v1/v2, the DS4 USB wireless adaptor, DualSense and DualSense Edge.</summary>
    public static PlayStationModel? ModelFor(ushort vendorId, ushort productId) => vendorId != SonyVendorId ? null : productId switch
    {
        0x05C4 or 0x09CC or 0x0BA0 => PlayStationModel.DualShock4,
        0x0CE6 or 0x0DF2 => PlayStationModel.DualSense,
        _ => null,
    };

    /// <summary>
    /// Parses one input report (report id in byte 0). <paramref name="bluetooth"/> matters for the DualSense,
    /// whose short Bluetooth report 0x01 uses the DualShock 4 layout. Returns null for reports that carry no input.
    /// </summary>
    public static PadSnapshot? Parse(ReadOnlySpan<byte> r, PlayStationModel model, bool bluetooth)
    {
        if (r.Length < 10) return null;
        // Offsets of: sticks (4 bytes), hat+face byte, buttons byte, PS byte, L2, R2.
        int stick, face, buttons, ps, l2, r2;
        switch (model, r[0])
        {
            case (PlayStationModel.DualShock4, 0x01):
            case (PlayStationModel.DualSense, 0x01) when bluetooth:
                (stick, face, buttons, ps, l2, r2) = (1, 5, 6, 7, 8, 9);
                break;
            case (PlayStationModel.DualShock4, 0x11):
                (stick, face, buttons, ps, l2, r2) = (3, 7, 8, 9, 10, 11);
                break;
            case (PlayStationModel.DualSense, 0x01):
                (stick, face, buttons, ps, l2, r2) = (1, 8, 9, 10, 5, 6);
                break;
            case (PlayStationModel.DualSense, 0x31):
                (stick, face, buttons, ps, l2, r2) = (2, 9, 10, 11, 6, 7);
                break;
            default:
                return null;
        }
        if (r.Length <= Math.Max(ps, Math.Max(l2, r2))) return null;

        ushort b = 0;
        b |= Hat(r[face] & 0x0F);
        if ((r[face] & 0x10) != 0) b |= PadSnapshot.X;   // Square
        if ((r[face] & 0x20) != 0) b |= PadSnapshot.A;   // Cross
        if ((r[face] & 0x40) != 0) b |= PadSnapshot.B;   // Circle
        if ((r[face] & 0x80) != 0) b |= PadSnapshot.Y;   // Triangle
        if ((r[buttons] & 0x01) != 0) b |= PadSnapshot.LeftShoulder;
        if ((r[buttons] & 0x02) != 0) b |= PadSnapshot.RightShoulder;
        if ((r[buttons] & 0x10) != 0) b |= PadSnapshot.Back;   // Share / Create
        if ((r[buttons] & 0x20) != 0) b |= PadSnapshot.Start;  // Options
        if ((r[buttons] & 0x40) != 0) b |= PadSnapshot.LeftThumb;
        if ((r[buttons] & 0x80) != 0) b |= PadSnapshot.RightThumb;
        if ((r[ps] & 0x01) != 0) b |= PadSnapshot.Guide;       // PS button

        return new PadSnapshot(b, Axis(r[stick]), (short)-Axis(r[stick + 1]), Axis(r[stick + 2]), (short)-Axis(r[stick + 3]), r[l2], r[r2]);
    }

    /// <summary>0..255 (128 centre) to -32767..32767.</summary>
    private static short Axis(byte v) => (short)Math.Clamp((v - 128) * 258, -32767, 32767);

    /// <summary>Hat switch 0 = N, clockwise to 7 = NW, 8 = released.</summary>
    private static ushort Hat(int h) => h switch
    {
        0 => PadSnapshot.Up,
        1 => PadSnapshot.Up | PadSnapshot.Right,
        2 => PadSnapshot.Right,
        3 => PadSnapshot.Down | PadSnapshot.Right,
        4 => PadSnapshot.Down,
        5 => PadSnapshot.Down | PadSnapshot.Left,
        6 => PadSnapshot.Left,
        7 => PadSnapshot.Up | PadSnapshot.Left,
        _ => 0,
    };
}
