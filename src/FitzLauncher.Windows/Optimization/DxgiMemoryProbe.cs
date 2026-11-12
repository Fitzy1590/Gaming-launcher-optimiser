using System.Runtime.InteropServices;
using FitzLauncher.Core.Optimization;

namespace FitzLauncher.Windows;

/// <summary>
/// Reads how much RAM Windows sees and how much is reserved for graphics. Uses GlobalMemoryStatusEx and DXGI
/// (CreateDXGIFactory1, IDXGIFactory1::EnumAdapters1, IDXGIAdapter1::GetDesc1) through raw vtable calls, no NuGet.
/// Only slots whose index is fixed by the DXGI headers are called (dxgi.h / dxgi1_2.h: IUnknown 0-2, IDXGIObject 3-6,
/// IDXGIFactory 7-11, IDXGIFactory1::EnumAdapters1 = 12; IDXGIAdapter 7-9, IDXGIAdapter1::GetDesc1 = 10).
/// Never throws: any failure gives a snapshot with DxgiOk = false.
/// </summary>
public static unsafe class DxgiMemoryProbe
{
    // IID_IDXGIFactory1 {770aae78-f26f-4dba-a829-253c83d1b387}
    private static readonly Guid FactoryIid = new(0x770aae78, 0xf26f, 0x4dba, 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87);
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint SoftwareAdapter = 2; // DXGI_ADAPTER_FLAG_SOFTWARE
    private const uint AmdVendor = 0x1002;

    // DXGI_ADAPTER_DESC1 (x64 size 312). SIZE_T fields are pointer-sized.
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    public static GraphicsMemorySnapshot Read()
    {
        var status = new Native.MemoryStatusEx { Length = (uint)sizeof(Native.MemoryStatusEx) };
        ulong total = 0, avail = 0;
        try { if (Native.GlobalMemoryStatusEx(ref status)) { total = status.TotalPhys; avail = status.AvailPhys; } }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }

        ulong dedicated = 0, shared = 0;
        var ok = false;
        try { ok = TryReadAdapter(out dedicated, out shared); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidOperationException) { }

        return new GraphicsMemorySnapshot
        {
            TotalPhysBytes = total,
            AvailPhysBytes = avail,
            DedicatedVideoBytes = dedicated,
            SharedSystemBytes = shared,
            DxgiOk = ok && total > 0,
        };
    }

    private static bool TryReadAdapter(out ulong dedicated, out ulong shared)
    {
        dedicated = shared = 0;
        if (CreateDXGIFactory1(in FactoryIid, out var factory) < 0 || factory == 0) return false;
        try
        {
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(void***)factory)[12];
            var found = false;
            uint bestScore = 0;
            for (uint i = 0; i < 16; i++)
            {
                nint adapter;
                var hr = enumAdapters1(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0 || adapter == 0) break;
                try
                {
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)(*(void***)adapter)[10];
                    AdapterDesc1 d = default;
                    if (getDesc1(adapter, &d) < 0 || (d.Flags & SoftwareAdapter) != 0) continue;
                    // Prefer an AMD adapter, then the one with the most graphics memory.
                    var score = (d.VendorId == AmdVendor ? 1u << 31 : 0u) | (uint)Math.Min((ulong)d.DedicatedVideoMemory >> 20, int.MaxValue);
                    if (found && score <= bestScore) continue;
                    found = true; bestScore = score;
                    dedicated = d.DedicatedVideoMemory;
                    shared = d.SharedSystemMemory;
                }
                finally { Release(adapter); }
            }
            return found;
        }
        finally { Release(factory); }
    }

    private static void Release(nint com) => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)com)[2])(com);
}
