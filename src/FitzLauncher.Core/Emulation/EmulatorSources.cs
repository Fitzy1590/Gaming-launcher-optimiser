using System.Text.Json;
using System.Text.Json.Serialization;

namespace FitzLauncher.Core.Emulation;

public enum EmulatorSourceType { GitHub, Website, Manual }

/// <summary>Where an emulator comes from and how its install is laid out (SPEC.md §1.1).</summary>
public sealed class EmulatorSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public EmuPlatform[] Platforms { get; set; } = [];
    public string[] ExeNames { get; set; } = [];
    public EmulatorSourceType Type { get; set; } = EmulatorSourceType.Manual;
    /// <summary>GitHub "owner/name" (GitHub sources only).</summary>
    public string? Repo { get; set; }
    /// <summary>Regex picking the Windows download from the release assets.</summary>
    public string? AssetPattern { get; set; }
    /// <summary>File whose presence makes the emulator keep its data beside the program.</summary>
    public string? PortableMarker { get; set; }
    /// <summary>Folders kept in the shared data folder so updates never touch them.</summary>
    public string[] DataDirs { get; set; } = [];
    /// <summary>False until the repo, asset pattern and folders were checked against the live project.</summary>
    public bool Verified { get; set; }
}

public sealed class EmulatorSourceFile
{
    public int Version { get; set; } = 1;
    public List<EmulatorSource> Emulators { get; set; } = [];
}

public static class EmulatorSources
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    // Everything here comes from SPEC.md §1.1 (written from memory) so Verified stays false until stage E2 checks it.
    public static IReadOnlyList<EmulatorSource> Defaults { get; } =
    [
        new() { Id = "pcsx2", Name = "PCSX2", Platforms = [EmuPlatform.PlayStation2], ExeNames = ["pcsx2-qt.exe"], Type = EmulatorSourceType.GitHub,
                Repo = "PCSX2/pcsx2", AssetPattern = @"windows.*x64.*\.(7z|zip)$", PortableMarker = "portable.ini", DataDirs = ["inis", "memcards", "sstates", "cache"] },
        new() { Id = "rpcs3", Name = "RPCS3", Platforms = [EmuPlatform.PlayStation3], ExeNames = ["rpcs3.exe"], Type = EmulatorSourceType.GitHub,
                Repo = "RPCS3/rpcs3-binaries-win", AssetPattern = @"win64.*\.(7z|zip)$" },
        new() { Id = "ppsspp", Name = "PPSSPP", Platforms = [EmuPlatform.PSP], ExeNames = ["PPSSPPWindows64.exe", "PPSSPPWindows.exe"], Type = EmulatorSourceType.Manual,
                PortableMarker = "memstick", DataDirs = ["memstick"] },
        new() { Id = "dolphin", Name = "Dolphin", Platforms = [EmuPlatform.GameCube, EmuPlatform.Wii], ExeNames = ["Dolphin.exe"], Type = EmulatorSourceType.Website,
                PortableMarker = "portable.txt", DataDirs = ["User"] },
        new() { Id = "lime3ds", Name = "Lime3DS", Platforms = [EmuPlatform.Nintendo3DS], ExeNames = ["lime3ds.exe", "lime-qt.exe"], Type = EmulatorSourceType.GitHub,
                Repo = "Lime3DS/lime3ds-archive", AssetPattern = @"windows.*\.(7z|zip)$", DataDirs = ["user"] },
        new() { Id = "azahar", Name = "Azahar", Platforms = [EmuPlatform.Nintendo3DS], ExeNames = ["azahar.exe", "azahar-qt.exe"], Type = EmulatorSourceType.GitHub,
                Repo = "azahar-emu/azahar", AssetPattern = @"windows.*\.(7z|zip)$", DataDirs = ["user"] },
        new() { Id = "xenia-canary", Name = "Xenia Canary", Platforms = [EmuPlatform.Xbox360], ExeNames = ["xenia_canary.exe"], Type = EmulatorSourceType.GitHub,
                Repo = "xenia-canary/xenia-canary-releases", AssetPattern = @"\.zip$", PortableMarker = "portable.txt", DataDirs = ["content"] },
        // Switch and Project64 sources are picked and checked at their own stages; until then the user points at the exe.
        new() { Id = "ryubing", Name = "Ryujinx (Ryubing fork)", Platforms = [EmuPlatform.Switch], ExeNames = ["Ryujinx.exe"], Type = EmulatorSourceType.Manual, DataDirs = ["portable"] },
        new() { Id = "yuzu-fork", Name = "Yuzu-style fork", Platforms = [EmuPlatform.Switch], ExeNames = ["yuzu.exe", "eden.exe"], Type = EmulatorSourceType.Manual, DataDirs = ["user"] },
        new() { Id = "rmg", Name = "Rosalie's Mupen GUI", Platforms = [EmuPlatform.N64], ExeNames = ["RMG.exe"], Type = EmulatorSourceType.GitHub,
                Repo = "Rosalie241/RMG", AssetPattern = @"windows.*\.(7z|zip)$" },
        new() { Id = "project64", Name = "Project64", Platforms = [EmuPlatform.N64], ExeNames = ["Project64.exe"], Type = EmulatorSourceType.Manual },
    ];

    /// <summary>Reads an edited <c>emulator-sources.json</c> over the defaults (same id replaces, new ids are added).
    /// Bad JSON keeps the defaults and says why.</summary>
    public static (List<EmulatorSource> Sources, string? Error) Merge(string? json)
    {
        var merged = Defaults.ToList();
        if (string.IsNullOrWhiteSpace(json)) return (merged, null);
        try
        {
            var file = JsonSerializer.Deserialize<EmulatorSourceFile>(json, Json);
            foreach (var e in file?.Emulators ?? [])
            {
                if (string.IsNullOrWhiteSpace(e.Id)) return (Defaults.ToList(), "An emulator in emulator-sources.json has no id.");
                var i = merged.FindIndex(x => x.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) merged[i] = e; else merged.Add(e);
            }
            return (merged, null);
        }
        catch (JsonException ex) { return (Defaults.ToList(), $"emulator-sources.json isn't valid: {ex.Message}"); }
    }

    public static string ToJson(IEnumerable<EmulatorSource> sources) => JsonSerializer.Serialize(new EmulatorSourceFile { Emulators = [.. sources] }, Json);
}
