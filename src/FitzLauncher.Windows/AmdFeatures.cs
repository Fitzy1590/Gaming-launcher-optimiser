using System.Runtime.InteropServices;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Storage;

namespace FitzLauncher.Windows;

/// <summary>
/// AMD Software features through AMD's ADLX library (amdadlx64.dll, installed with the Radeon driver):
/// Anti-Lag, Radeon Chill, Radeon Super Resolution, AMD Fluid Motion Frames, Radeon Image Sharpening and
/// Radeon Boost. ADLX is a C interface of function tables; these calls follow the layouts in AMD's published
/// SDK headers (GPUOpen ADLX 2.0). Settings are global, so a session applies the game's profile and restores
/// the previous values afterwards (and at the next start after a crash).
/// </summary>
public sealed unsafe class AmdFeatures : IDisposable
{
    private const int Ok = 0, AlreadyEnabled = 1;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);

    private nint _dll, _system, _services, _services1, _gpu;

    public string? Error { get; private set; }

    private AmdFeatures() { }

    /// <summary>Opens ADLX; null (with a reason) when there's no AMD driver with ADLX on this PC.</summary>
    public static AmdFeatures? Open(out string? error)
    {
        var a = new AmdFeatures();
        error = a.Init();
        if (error is null) return a;
        a.Dispose();
        return null;
    }

    private static bool Ok_(int r) => r is Ok or AlreadyEnabled or 2; // ADLX_ALREADY_INITIALIZED = 2

    private string? Init()
    {
        _dll = LoadLibrary("amdadlx64.dll");
        if (_dll == 0) return "AMD's ADLX library wasn't found. It comes with recent AMD Software (Adrenalin) drivers.";
        var query = GetProcAddress(_dll, "ADLXQueryFullVersion");
        var init = GetProcAddress(_dll, "ADLXInitialize");
        if (init == 0) return "This AMD driver's ADLX doesn't offer initialisation.";
        ulong version = (2UL << 48) | 125; // ADLX 2.0.0.125, the SDK these layouts come from
        if (query != 0)
        {
            ulong v;
            if (((delegate* unmanaged[Cdecl]<ulong*, int>)query)(&v) == Ok && v != 0) version = Math.Min(v, version);
        }
        nint system;
        var r = ((delegate* unmanaged[Cdecl]<ulong, nint*, int>)init)(version, &system);
        if (!Ok_(r) || system == 0) return $"ADLX didn't start (code {r}). Updating AMD Software usually fixes this.";
        _system = system;

        // IADLXSystem: [1] GetGPUs, [7] Get3DSettingsServices (no Acquire/Release on the system object)
        nint list;
        if (!Ok_(Call<nint>(_system, 1, &list)) || list == 0) return "ADLX found no AMD GPU.";
        nint gpu;
        // IADLXGPUList: [3] Size, [11] At_GPUList, [1] Release
        var r2 = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(list)[11])(list, 0, &gpu);
        ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(list)[1])(list);
        if (!Ok_(r2) || gpu == 0) return "ADLX found no AMD GPU.";
        _gpu = gpu;
        nint services;
        if (!Ok_(Call<nint>(_system, 7, &services)) || services == 0) return "ADLX's 3D settings aren't available on this driver.";
        _services = services;
        // IADLX3DSettingsServices1 (adds Fluid Motion Frames) via QueryInterface [2]
        var iid = Marshal.StringToHGlobalUni("IADLX3DSettingsServices1");
        try
        {
            nint s1;
            if (Ok_(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vtbl(_services)[2])(_services, iid, &s1))) _services1 = s1;
        }
        finally { Marshal.FreeHGlobal(iid); }
        return null;
    }

    private static nint* Vtbl(nint obj) => *(nint**)obj;
    private static int Call<T>(nint obj, int slot, nint* outPtr) => ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(obj)[slot])(obj, outPtr);
    private static void Release(nint obj) { if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(obj)[1])(obj); }

    // IADLX3DSettingsServices: [3] GetAntiLag(gpu) [4] GetChill(gpu) [5] GetBoost(gpu) [6] GetImageSharpening(gpu) [14] GetRadeonSuperResolution() ; Services1 [17] GetAMDFluidMotionFrames()
    private nint Feature(int slot, bool perGpu)
    {
        var svc = slot == 17 ? _services1 : _services;
        if (svc == 0) return 0;
        nint f;
        var r = perGpu
            ? ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vtbl(svc)[slot])(svc, _gpu, &f)
            : ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(svc)[slot])(svc, &f);
        return Ok_(r) ? f : 0;
    }

    private static bool? GetBool(nint f, int slot)
    {
        byte b;
        return f != 0 && Ok_(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Vtbl(f)[slot])(f, &b)) ? b != 0 : null;
    }

    private static int? GetInt(nint f, int slot)
    {
        int v;
        return f != 0 && Ok_(((delegate* unmanaged[Stdcall]<nint, int*, int>)Vtbl(f)[slot])(f, &v)) ? v : null;
    }

    private static bool SetBool(nint f, int slot, bool on) => f != 0 && Ok_(((delegate* unmanaged[Stdcall]<nint, byte, int>)Vtbl(f)[slot])(f, on ? (byte)1 : (byte)0));
    private static bool SetInt(nint f, int slot, int v) => f != 0 && Ok_(((delegate* unmanaged[Stdcall]<nint, int, int>)Vtbl(f)[slot])(f, v));

    /// <summary>Each feature interface: [3] IsSupported, [4] IsEnabled.</summary>
    private static bool Supported(nint f) => GetBool(f, 3) == true;

    private T With<T>(int slot, bool perGpu, Func<nint, T> use)
    {
        var f = Feature(slot, perGpu);
        try { return use(f); }
        finally { Release(f); }
    }

    /// <summary>Which features this GPU and driver support.</summary>
    public List<string> SupportedFeatures()
    {
        var list = new List<string>();
        if (With(3, true, Supported)) list.Add("Anti-Lag");
        if (With(4, true, Supported)) list.Add("Radeon Chill");
        if (With(14, false, Supported)) list.Add("Radeon Super Resolution");
        if (With(17, false, Supported)) list.Add("Fluid Motion Frames");
        if (With(6, true, Supported)) list.Add("Image Sharpening");
        if (With(5, true, Supported)) list.Add("Radeon Boost");
        return list;
    }

    public AmdFeatureState Read() => new()
    {
        AntiLag = With(3, true, f => Supported(f) ? GetBool(f, 4) : null),
        Chill = With(4, true, f => Supported(f) ? GetBool(f, 4) : null),
        ChillMinFps = With(4, true, f => Supported(f) ? GetInt(f, 6) : null),
        ChillMaxFps = With(4, true, f => Supported(f) ? GetInt(f, 7) : null),
        SuperResolution = With(14, false, f => Supported(f) ? GetBool(f, 4) : null),
        FluidMotionFrames = With(17, false, f => Supported(f) ? GetBool(f, 4) : null),
        ImageSharpening = With(6, true, f => Supported(f) ? GetBool(f, 4) : null),
        SharpeningPercent = With(6, true, f => Supported(f) ? GetInt(f, 6) : null),
        Boost = With(5, true, f => Supported(f) ? GetBool(f, 4) : null),
    };

    /// <summary>Applies a profile; returns what was changed (skips unsupported features).</summary>
    public List<string> Apply(AmdProfile p)
    {
        var done = new List<string>();
        if (p.AntiLag is { } al && With(3, true, f => Supported(f) && SetBool(f, 5, al))) done.Add($"Anti-Lag {(al ? "on" : "off")}");
        if (p.Chill is { } ch && With(4, true, f =>
            {
                if (!Supported(f)) return false;
                if (ch) { SetInt(f, 9, Math.Min(p.ChillMinFps, p.ChillMaxFps)); SetInt(f, 10, Math.Max(p.ChillMinFps, p.ChillMaxFps)); }
                return SetBool(f, 8, ch);
            })) done.Add(ch ? $"Chill {p.ChillMinFps}–{p.ChillMaxFps} FPS" : "Chill off");
        if (p.SuperResolution is { } rsr && With(14, false, f => Supported(f) && SetBool(f, 5, rsr))) done.Add($"Super Resolution {(rsr ? "on" : "off")}");
        if (p.FluidMotionFrames is { } fmf && With(17, false, f => Supported(f) && SetBool(f, 5, fmf))) done.Add($"Fluid Motion Frames {(fmf ? "on" : "off")}");
        if (p.ImageSharpening is { } ris && With(6, true, f =>
            {
                if (!Supported(f)) return false;
                if (ris) SetInt(f, 8, Math.Clamp(p.SharpeningPercent, 10, 100));
                return SetBool(f, 7, ris);
            })) done.Add(ris ? $"Image Sharpening {p.SharpeningPercent}%" : "Image Sharpening off");
        if (p.Boost is { } bo && With(5, true, f => Supported(f) && SetBool(f, 7, bo))) done.Add($"Boost {(bo ? "on" : "off")}");
        return done;
    }

    /// <summary>Puts back the values saved before the session.</summary>
    public void Restore(AmdFeatureState s)
    {
        Apply(new AmdProfile
        {
            AntiLag = s.AntiLag, Chill = s.Chill, ChillMinFps = s.ChillMinFps ?? 60, ChillMaxFps = s.ChillMaxFps ?? 144,
            SuperResolution = s.SuperResolution, FluidMotionFrames = s.FluidMotionFrames,
            ImageSharpening = s.ImageSharpening, SharpeningPercent = s.SharpeningPercent ?? 80, Boost = s.Boost,
        });
    }

    public void Dispose()
    {
        Release(_services1);
        Release(_services);
        Release(_gpu);
        if (_dll != 0)
        {
            var term = GetProcAddress(_dll, "ADLXTerminate");
            if (_system != 0 && term != 0) ((delegate* unmanaged[Cdecl]<int>)term)();
            FreeLibrary(_dll);
        }
        _services1 = _services = _gpu = _system = _dll = 0;
    }

    // ------------------------------------------------------------------ session helpers with crash-safe restore

    public static string RestoreFile(AppPaths p) => Path.Combine(p.Root, "amd-restore.json");

    /// <summary>Applies a game's profile, saving the previous state first. Returns a status line, or null.</summary>
    public static string? ApplyForSession(AppPaths paths, AmdProfile profile)
    {
        if (!profile.Any) return null;
        using var amd = Open(out var err);
        if (amd is null) return $"AMD features: {err}";
        if (!File.Exists(RestoreFile(paths))) Json.Save(RestoreFile(paths), amd.Read());
        var done = amd.Apply(profile);
        return done.Count == 0 ? "AMD features: none of the chosen features are supported here." : "AMD: " + string.Join(", ", done);
    }

    public static void RestoreAfterSession(AppPaths paths)
    {
        if (Json.Load<AmdFeatureState>(RestoreFile(paths)) is not { } prev) return;
        using var amd = Open(out _);
        if (amd is null) return;
        amd.Restore(prev);
        try { File.Delete(RestoreFile(paths)); } catch (IOException) { }
    }
}
