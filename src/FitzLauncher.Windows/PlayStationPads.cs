using System.Runtime.InteropServices;
using FitzLauncher.Core.Input;
using Microsoft.Win32.SafeHandles;

namespace FitzLauncher.Windows;

/// <summary>
/// Reads Sony DualShock 4 / DualSense controllers directly over HID (USB or Bluetooth), so they work in the
/// launcher's menus without DS4Windows or Steam Input. Opens the controller shared, so games, Steam and
/// DS4Windows (non-exclusive) still see it. One reader thread per controller; looks for new ones every few
/// seconds while none is connected.
/// </summary>
public sealed class PlayStationPads : IDisposable
{
    public static PlayStationPads Shared { get; } = new();

    private readonly object _gate = new();
    private readonly List<Reader> _readers = [];
    private Timer? _scan;
    private int _users;
    private DateTime _lastScan;

    /// <summary>The newest reading from any connected Sony controller (null when none).</summary>
    public PadSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                PadSnapshot? best = null;
                foreach (var r in _readers.Where(r => r.Latest is not null))
                    if (best is null || r.Latest!.Value.Buttons != 0) best = r.Latest;
                return best;
            }
        }
    }

    public IReadOnlyList<string> Connected { get { lock (_gate) return _readers.Select(r => r.Name).ToList(); } }

    /// <summary>Starts reading (reference counted: each caller that starts it should stop it).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_users++ > 0) return;
            _scan = new Timer(_ => Scan(), null, 0, 3000);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_users == 0 || --_users > 0) return;
            _scan?.Dispose();
            _scan = null;
            foreach (var r in _readers) r.Dispose();
            _readers.Clear();
        }
    }

    private void Scan()
    {
        try
        {
            lock (_gate)
            {
                _readers.RemoveAll(r => r.Closed);
                if (_users == 0) return;
                // With a pad connected, look for a second one only every 15 s.
                if (_readers.Count > 0 && DateTime.UtcNow - _lastScan < TimeSpan.FromSeconds(15)) return;
                _lastScan = DateTime.UtcNow;
            }
            foreach (var (path, model, bt, length, name) in Find())
            {
                lock (_gate)
                {
                    if (_readers.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                    if (Reader.Open(path, model, bt, length, name) is { } reader) _readers.Add(reader);
                }
            }
        }
        catch (Exception e) when (e is ExternalException or IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _scan?.Dispose();
            foreach (var r in _readers) r.Dispose();
            _readers.Clear();
            _users = 0;
        }
    }

    // ------------------------------------------------------------------ one controller

    private sealed class Reader : IDisposable
    {
        private readonly SafeFileHandle _h;
        private readonly PlayStationModel _model;
        private readonly bool _bt;
        private readonly int _length;
        private volatile bool _closed;
        private PadSnapshot? _latest;

        public string Path { get; }
        public string Name { get; }
        public bool Closed => _closed;
        public PadSnapshot? Latest { get { lock (this) return _closed ? null : _latest; } }

        private Reader(SafeFileHandle h, string path, PlayStationModel model, bool bt, int length, string name)
        {
            (_h, Path, _model, _bt, _length, Name) = (h, path, model, bt, length, name);
            new Thread(Loop) { IsBackground = true, Name = "Sony pad reader", Priority = ThreadPriority.AboveNormal }.Start();
        }

        public static Reader? Open(string path, PlayStationModel model, bool bt, int length, string name)
        {
            var h = Hid.Open(path, write: false);
            if (h.IsInvalid) { h.Dispose(); return null; }
            return new Reader(h, path, model, bt, length, name);
        }

        private void Loop()
        {
            var buf = new byte[Math.Max(_length, 64)];
            try
            {
                while (!_closed)
                {
                    if (!Hid.ReadFile(_h, buf, buf.Length, out var read, 0) || read <= 0) break; // unplugged or closed
                    if (PlayStationReport.Parse(buf.AsSpan(0, read), _model, _bt) is { } s) lock (this) _latest = s;
                }
            }
            catch (ObjectDisposedException) { }
            _closed = true;
        }

        public void Dispose()
        {
            _closed = true;
            Hid.CancelIoEx(_h, 0);
            _h.Dispose();
        }
    }

    // ------------------------------------------------------------------ discovery

    private static IEnumerable<(string path, PlayStationModel model, bool bluetooth, int inputLength, string name)> Find()
    {
        var list = new List<(string, PlayStationModel, bool, int, string)>();
        Hid.HidD_GetHidGuid(out var guid);
        var set = Hid.SetupDiGetClassDevs(ref guid, 0, 0, 0x12 /* PRESENT | DEVICEINTERFACE */);
        if (set == -1) return list;
        try
        {
            var data = new Hid.DeviceInterfaceData { cbSize = Marshal.SizeOf<Hid.DeviceInterfaceData>() };
            for (uint i = 0; Hid.SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref data); i++)
            {
                Hid.SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out var size, 0);
                if (size <= 0) continue;
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Hid.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, 0)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (string.IsNullOrEmpty(path)) continue;
                    using var h = Hid.Open(path, write: false, access: 0); // attributes only: never blocked
                    if (h.IsInvalid) continue;
                    var attr = new Hid.Attributes { Size = Marshal.SizeOf<Hid.Attributes>() };
                    if (!Hid.HidD_GetAttributes(h, ref attr) || PlayStationReport.ModelFor(attr.VendorID, attr.ProductID) is not { } model) continue;
                    if (!Hid.HidD_GetPreparsedData(h, out var pp)) continue;
                    var caps = new ushort[32];
                    try { if (Hid.HidP_GetCaps(pp, caps) != 0x00110000) continue; }
                    finally { Hid.HidD_FreePreparsedData(pp); }
                    if (caps[1] != 0x01 || caps[0] != 0x05) continue; // Generic Desktop / Game Pad collection only
                    var inputLength = caps[2];
                    // USB reports are 64 bytes; Bluetooth ones are longer (78 or 547).
                    var bt = inputLength != 64;
                    var name = (model == PlayStationModel.DualSense ? (attr.ProductID == 0x0DF2 ? "DualSense Edge" : "DualSense") : "DualShock 4") + (bt ? " (Bluetooth)" : " (USB)");
                    list.Add((path, model, bt, inputLength, name));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Hid.SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    private static class Hid
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInterfaceData { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public nint Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Attributes { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attributes a);
        [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out nint data);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(nint data);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(nint data, [Out] ushort[] caps);

        [DllImport("setupapi.dll", SetLastError = true)] public static extern nint SetupDiGetClassDevs(ref Guid g, nint enumerator, nint parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid g, uint index, ref DeviceInterfaceData data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterfaceData data, nint detail, int size, out int required, nint devInfo);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(nint set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, nint overlapped);
        [DllImport("kernel32.dll")] public static extern bool CancelIoEx(SafeFileHandle h, nint overlapped);

        public static SafeFileHandle Open(string path, bool write, uint? access = null) =>
            CreateFile(path, access ?? (0x80000000u | (write ? 0x40000000u : 0)), 3 /* share read|write */, 0, 3 /* OPEN_EXISTING */, 0, 0);
    }
}
