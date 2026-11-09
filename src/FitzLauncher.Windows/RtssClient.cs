using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace FitzLauncher.Windows;

/// <summary>
/// Talks to RivaTuner Statistics Server without changing how you have it (or Afterburner) set up:
/// <list type="bullet">
/// <item>Live per-game frame limits through RTSSHooks64.dll's profile API, so the thermal guard can
/// lower the cap mid-game and put it back afterwards.</item>
/// <item>One line in the on-screen display through RTSS's shared memory, in our own OSD slot.
/// Afterburner's lines are untouched and ours is removed when the game ends.</item>
/// </list>
/// </summary>
public sealed class RtssClient(string rtssDirectory) : IDisposable
{
    private const string Owner = "FitzLauncher";
    private const uint RtssSignature = 0x52545353; // 'RTSS'
    private nint _hooks;
    private bool _hooksTried;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate void ProfileFn([MarshalAs(UnmanagedType.LPStr)] string profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate bool SetPropertyFn([MarshalAs(UnmanagedType.LPStr)] string name, ref int data, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UpdateFn();

    public static bool IsRunning => System.Diagnostics.Process.GetProcessesByName("RTSS").Length > 0;

    /// <summary>Changes the frame limit for one exe immediately (also saved to its profile).</summary>
    /// <summary>
    /// Turns RTSS's on-screen display (every app's lines, Afterburner's included) off or on for one game's profile,
    /// via the same RTSSHooks profile API as the frame limit ("EnableOSD" is the profile's [OSD] key).
    /// </summary>
    public bool SetOsdEnabled(string exeName, bool on) => SetProfileInt(exeName, "EnableOSD", on ? 1 : 0);

    private bool SetProfileInt(string exeName, string property, int value)
    {
        if (!LoadHooks()) return false;
        try
        {
            var load = Fn<ProfileFn>("LoadProfile");
            var set = Fn<SetPropertyFn>("SetProfileProperty");
            var save = Fn<ProfileFn>("SaveProfile");
            var update = Fn<UpdateFn>("UpdateProfiles");
            if (load is null || set is null || save is null || update is null) return false;
            load(exeName);
            var v = value;
            var ok = set(property, ref v, sizeof(int));
            save(exeName);
            update();
            return ok;
        }
        catch (Exception e) when (e is SEHException or AccessViolationException or EntryPointNotFoundException) { return false; }
    }

    public bool SetFramerateLimit(string exeName, int fps)
    {
        if (!LoadHooks()) return false;
        try
        {
            var load = Fn<ProfileFn>("LoadProfile");
            var set = Fn<SetPropertyFn>("SetProfileProperty");
            var save = Fn<ProfileFn>("SaveProfile");
            var update = Fn<UpdateFn>("UpdateProfiles");
            if (load is null || set is null || save is null || update is null) return false;
            load(exeName);
            var value = fps;
            set("FramerateLimit", ref value, sizeof(int));
            save(exeName);
            update();
            return true;
        }
        catch (Exception e) when (e is SEHException or AccessViolationException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// Makes RTSS pick up a profile file we edited on disk (frame limit, limiter mode): load it into RTSS,
    /// save it back and notify the hooked apps, the same sequence RTSS's own SDK tools use.
    /// </summary>
    public bool ReloadProfile(string exeName)
    {
        if (!LoadHooks()) return false;
        try
        {
            var load = Fn<ProfileFn>("LoadProfile");
            var save = Fn<ProfileFn>("SaveProfile");
            var update = Fn<UpdateFn>("UpdateProfiles");
            if (load is null || save is null || update is null) return false;
            load(exeName);
            save(exeName);
            update();
            return true;
        }
        catch (Exception e) when (e is SEHException or AccessViolationException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// True when this RTSS version stores the limiter mode as [Framerate] SyncLimiter in its Global
    /// profile. The launcher only writes that key when RTSS itself already uses it.
    /// </summary>
    public static bool SupportsLimiterMode(string rtssDirectory)
    {
        var global = Path.Combine(rtssDirectory, "Profiles", "Global");
        if (!File.Exists(global)) return false;
        try { return FitzLauncher.Core.Config.IniDocument.Load(global).Get("Framerate", "SyncLimiter") is not null; }
        catch (IOException) { return false; }
    }

    private bool LoadHooks()
    {
        if (_hooksTried) return _hooks != 0;
        _hooksTried = true;
        var path = Path.Combine(rtssDirectory, Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll");
        return File.Exists(path) && NativeLibrary.TryLoad(path, out _hooks);
    }

    private T? Fn<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_hooks, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

    // ------------------------------------------------------------------ OSD

    /// <summary>
    /// The text other apps (MSI Afterburner's monitoring lines, RTSS's own statistics) currently show on the OSD, so we don't
    /// show the same numbers a second time. Empty when RTSS isn't running or nothing else is displaying.
    /// </summary>
    public static string ReadOtherOsdText()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != RtssSignature) return "";
            var version = view.ReadUInt32(4);
            if (version < 0x00020000) return "";
            var entrySize = view.ReadUInt32(20);
            var arrOffset = view.ReadUInt32(24);
            var arrSize = view.ReadUInt32(28);
            var sb = new System.Text.StringBuilder();
            for (uint i = 0; i < arrSize; i++)
            {
                long entry = arrOffset + i * entrySize;
                if (ReadString(view, entry + 256, 256) == Owner) continue;
                var text = version >= 0x00020007 ? ReadString(view, entry + 512, 4096) : ReadString(view, entry, 256);
                if (text.Length > 0) sb.Append(text).Append('\n');
            }
            return sb.ToString();
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Writes our OSD line. Returns false if RTSS isn't running.</summary>
    public static bool SetOsdText(string text)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            if (view.ReadUInt32(0) != RtssSignature) return false;
            var version = view.ReadUInt32(4);
            if (version < 0x00020000) return false;
            var entrySize = view.ReadUInt32(20);
            var arrOffset = view.ReadUInt32(24);
            var arrSize = view.ReadUInt32(28);

            // Same two-pass slot claim as RTSS's own SDK sample: reuse our slot, else take a free one.
            for (var pass = 0; pass < 2; pass++)
            {
                for (uint i = 1; i < arrSize; i++)
                {
                    long entry = arrOffset + i * entrySize;
                    var owner = ReadString(view, entry + 256, 256);
                    if (pass == 1 && owner.Length == 0) { WriteString(view, entry + 256, 256, Owner); owner = Owner; }
                    if (owner != Owner) continue;
                    if (version >= 0x00020007) WriteString(view, entry + 512, 4096, text);
                    else WriteString(view, entry, 256, text);
                    view.Write(32, view.ReadUInt32(32) + 1); // dwOSDFrame++ tells RTSS to redraw
                    return true;
                }
            }
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>Releases our OSD slot.</summary>
    public static void ClearOsd()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            if (view.ReadUInt32(0) != RtssSignature) return;
            var entrySize = view.ReadUInt32(20);
            var arrOffset = view.ReadUInt32(24);
            var arrSize = view.ReadUInt32(28);
            for (uint i = 1; i < arrSize; i++)
            {
                long entry = arrOffset + i * entrySize;
                if (ReadString(view, entry + 256, 256) != Owner) continue;
                WriteString(view, entry, 256, "");
                WriteString(view, entry + 512, 4096, "");
                WriteString(view, entry + 256, 256, "");
                view.Write(32, view.ReadUInt32(32) + 1);
            }
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { }
    }

    private static string ReadString(MemoryMappedViewAccessor v, long offset, int max)
    {
        var bytes = new byte[max];
        v.ReadArray(offset, bytes, 0, max);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.Latin1.GetString(bytes, 0, end < 0 ? max : end); // RTSS text is ANSI: ° (0xB0) must survive for the temperature check
    }

    private static void WriteString(MemoryMappedViewAccessor v, long offset, int max, string s)
    {
        var bytes = new byte[max];
        // RTSS reads OSD text as ANSI; Latin-1 covers ° and · so temperatures render as "77°C".
        var text = Encoding.Latin1.GetBytes(s);
        Array.Copy(text, bytes, Math.Min(text.Length, max - 1));
        v.WriteArray(offset, bytes, 0, max);
    }

    public void Dispose()
    {
        // RTSSHooks stays loaded (RTSS injects it into every process anyway); nothing to free.
    }
}
