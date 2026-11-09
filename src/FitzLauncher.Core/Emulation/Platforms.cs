namespace FitzLauncher.Core.Emulation;

public enum EmuPlatform { Switch, Nintendo3DS, PlayStation2, PlayStation3, PSP, GameCube, Wii, N64, Xbox360 }

/// <summary>One console the hub can organise: its file types, IGDB platform id and the emulators that run it.</summary>
public sealed record PlatformInfo(EmuPlatform Platform, string Name, string[] Extensions, int? IgdbPlatformId, string[] EmulatorIds);

public static class Platforms
{
    // IGDB platform ids and file extensions are from memory (SPEC.md §2.2): checked in stage E4.
    public static IReadOnlyList<PlatformInfo> All { get; } =
    [
        new(EmuPlatform.Switch, "Nintendo Switch", [".nsp", ".xci"], 130, ["ryubing", "yuzu-fork"]),
        new(EmuPlatform.Nintendo3DS, "Nintendo 3DS", [".3ds", ".cci", ".cxi", ".cia", ".3dsx"], 37, ["lime3ds", "azahar"]),
        new(EmuPlatform.PlayStation2, "PlayStation 2", [".iso", ".chd", ".cso", ".bin"], 8, ["pcsx2"]),
        new(EmuPlatform.PlayStation3, "PlayStation 3", [".iso", ".pkg"], 9, ["rpcs3"]),
        new(EmuPlatform.PSP, "PlayStation Portable", [".iso", ".cso", ".pbp"], 38, ["ppsspp"]),
        new(EmuPlatform.GameCube, "GameCube", [".iso", ".gcm", ".rvz", ".gcz", ".ciso"], 21, ["dolphin"]),
        new(EmuPlatform.Wii, "Wii", [".wbfs", ".wad", ".iso", ".rvz"], 5, ["dolphin"]),
        new(EmuPlatform.N64, "Nintendo 64", [".z64", ".n64", ".v64"], 4, ["rmg", "project64"]),
        new(EmuPlatform.Xbox360, "Xbox 360", [".xex", ".iso"], 12, ["xenia-canary"]),
    ];

    public static PlatformInfo Get(EmuPlatform p) => All.First(x => x.Platform == p);

    /// <summary>Platforms whose files use this extension (".iso" belongs to several: the caller disambiguates).</summary>
    public static IReadOnlyList<PlatformInfo> ByExtension(string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return [.. All.Where(p => p.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))];
    }
}
