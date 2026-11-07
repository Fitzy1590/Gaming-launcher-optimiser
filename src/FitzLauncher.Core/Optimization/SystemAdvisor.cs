using FitzLauncher.Core.Models;

namespace FitzLauncher.Core.Optimization;

/// <summary>Machine-wide recommendations derived from the hardware scan (shown on the Hardware page).</summary>
public static class SystemAdvisor
{
    public static List<Tweak> Analyse(HardwareProfile hw, IEnumerable<Game>? games = null)
    {
        var list = new List<Tweak>();
        var gpu = hw.PrimaryGpu;

        // Display refresh: a surprisingly common free win.
        foreach (var d in hw.Displays.Where(d => d.MaxRefreshHz > d.CurrentRefreshHz + 5))
        {
            list.Add(new Tweak
            {
                Id = "display-refresh-" + d.DeviceName,
                Title = $"Display is running at {d.CurrentRefreshHz} Hz but supports {d.MaxRefreshHz} Hz",
                Description = $"Windows has {d.DeviceName} at {d.CurrentRefreshHz} Hz. At that refresh rate the 90-117 FPS targets can't actually be seen.",
                Category = TweakCategory.Hardware,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "Smoother motion immediately",
                Actions = [new ManualStepAction { Instructions = $"Settings → System → Display → Advanced display → Choose a refresh rate → {d.MaxRefreshHz} Hz." }],
                Recommended = true,
            });
        }

        if (hw.WindowsGameModeEnabled == false)
        {
            list.Add(new Tweak
            {
                Id = "win-game-mode",
                Title = "Turn on Windows Game Mode",
                Description = "Windows Game Mode stops Windows Update from installing drivers or showing restart prompts during play, and gives the game priority for CPU scheduling.",
                Category = TweakCategory.System,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "Better 1% lows, no update interruptions",
                Actions = [new RegistrySetAction { Hive = RegistryHive.CurrentUser, KeyPath = @"Software\Microsoft\GameBar", ValueName = "AutoGameModeEnabled", Value = "1", ValueType = RegistryValueType.DWord }],
                Recommended = true,
            });
        }

        if (hw.GameDvrBackgroundRecording == true)
        {
            list.Add(new Tweak
            {
                Id = "game-dvr-off",
                Title = "Stop Xbox Game Bar background recording",
                Description = "Background recording continuously encodes the last few minutes of gameplay. That costs GPU encoder time and VRAM, and can cause frame-time spikes.",
                Category = TweakCategory.System,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "+1-5% FPS, steadier frame times",
                // Only the always-on "record what happened" buffer; manual screenshots/clips keep working.
                Actions = [new RegistrySetAction { Hive = RegistryHive.CurrentUser, KeyPath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR", ValueName = "HistoricalCaptureEnabled", Value = "0", ValueType = RegistryValueType.DWord }],
                Recommended = true,
            });
        }

        var modernGpu = gpu is not null && (gpu.Name.Contains("RTX 40") || gpu.Name.Contains("RTX 50") ||
                                            gpu.Name.Contains("RX 7") || gpu.Name.Contains("RX 9"));
        if (hw.HagsEnabled == false)
        {
            list.Add(new Tweak
            {
                Id = "hags-on",
                Title = "Turn on Hardware-accelerated GPU scheduling",
                Description = modernGpu
                    ? "Your GPU supports frame generation (DLSS 3/4, FSR 3 FG), which requires HAGS. It also slightly reduces CPU overhead."
                    : "HAGS moves GPU scheduling onto the GPU itself. Results vary by game, so compare your recorded FPS before and after.",
                Category = TweakCategory.System,
                VisualImpact = VisualImpact.None,
                Risk = TweakRisk.Moderate,
                NeedsRestart = true,
                ExpectedGain = modernGpu ? "Enables frame generation" : "0-3% FPS (varies)",
                Actions = [new RegistrySetAction { Hive = RegistryHive.LocalMachine, KeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "HwSchMode", Value = "2", ValueType = RegistryValueType.DWord }],
                Recommended = modernGpu,
            });
        }

        if (hw.MemoryIntegrityEnabled == true)
        {
            list.Add(new Tweak
            {
                Id = "memory-integrity",
                Title = "Memory Integrity (Core isolation) is on",
                Description = "Memory Integrity runs part of Windows in a hypervisor. Benchmarks, and Microsoft's own guidance, show it costs roughly 3-10% FPS in some games. Turning it off lowers protection against kernel-level malware, so the launcher leaves this decision to you.",
                Category = TweakCategory.System,
                VisualImpact = VisualImpact.None,
                Risk = TweakRisk.Moderate,
                NeedsRestart = true,
                ExpectedGain = "+3-10% FPS in some games",
                Actions = [new ManualStepAction { Instructions = "Windows Security → Device security → Core isolation details → Memory integrity → Off (restart required)." }],
            });
        }

        // RAM speed: JEDEC defaults usually mean XMP/EXPO is off.
        var jedec = (hw.RamType == "DDR4" && hw.RamConfiguredMhz is > 0 and <= 2666) ||
                    (hw.RamType == "DDR5" && hw.RamConfiguredMhz is > 0 and <= 4800);
        if (jedec)
        {
            list.Add(new Tweak
            {
                Id = "xmp",
                Title = $"RAM is running at {hw.RamConfiguredMhz} MT/s, likely the default speed",
                Description = $"{hw.RamType} at {hw.RamConfiguredMhz} MT/s is the JEDEC fallback speed. If your kit is rated faster (most gaming kits are DDR4-3200/3600 or DDR5-6000), turning on XMP/EXPO in the BIOS is one of the biggest free FPS gains available, especially on Ryzen CPUs.",
                Category = TweakCategory.Hardware,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "+5-15% FPS in CPU-bound games",
                Actions = [new ManualStepAction { Instructions = "Restart into the BIOS (usually Del/F2) and enable XMP (Intel) or EXPO/DOCP (AMD), then save and exit." }],
                Recommended = true,
            });
        }

        if (hw.RamSticks == 1)
        {
            list.Add(new Tweak
            {
                Id = "dual-channel",
                Title = "Single stick of RAM (single-channel)",
                Description = "One memory stick halves memory bandwidth. Adding a matching second stick (dual-channel) is a big uplift for CPU-bound games and for integrated graphics.",
                Category = TweakCategory.Hardware,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "+10-30% 1% lows",
                Actions = [new ManualStepAction { Instructions = "Add a second identical RAM stick in the slot the motherboard manual lists for dual-channel (usually A2 + B2)." }],
                Recommended = true,
            });
        }

        if (hw.RamGb is > 0 and < 15)
        {
            list.Add(new Tweak
            {
                Id = "low-ram",
                Title = $"{hw.RamGb:0} GB RAM: memory guard switched to aggressive",
                Description = "Modern games commonly use 10-14 GB. Game Mode will clear standby memory more aggressively and close more background apps on this PC. 16-32 GB would remove most memory-related stutter.",
                Category = TweakCategory.Info,
                VisualImpact = VisualImpact.None,
                Actions = [new ManualStepAction { Instructions = "Consider upgrading to 16-32 GB." }],
            });
        }

        if (hw.IsLaptop)
        {
            list.Add(new Tweak
            {
                Id = "laptop",
                Title = hw.OnBattery ? "Laptop is on battery" : "Laptop detected",
                Description = (hw.OnBattery ? "On battery, the GPU and CPU are heavily power-limited. Plug in before gaming. " : "") +
                              "Use your laptop maker's performance mode (e.g. Armoury Crate Turbo, Legion Performance) and keep the vents clear. If your laptop has a MUX switch, select 'dGPU only' in the vendor app for 5-15% more FPS.",
                Category = TweakCategory.Hardware,
                VisualImpact = VisualImpact.None,
                Actions = [new ManualStepAction { Instructions = "Plug in, select the vendor performance mode, and set the MUX switch to dGPU if available." }],
                Recommended = hw.OnBattery,
            });
        }

        if (hw.CpuHasVCache)
        {
            list.Add(new Tweak
            {
                Id = "x3d",
                Title = "AMD X3D CPU detected",
                Description = "X3D chips rely on the Balanced power plan and the AMD chipset driver to park the non-V-Cache cores during games. Game Mode leaves your power plan on Balanced for this reason. Keep the AMD chipset driver and Xbox Game Bar up to date.",
                Category = TweakCategory.Info,
                VisualImpact = VisualImpact.None,
                Actions = [new ManualStepAction { Instructions = "Install the latest AMD chipset driver; keep the Balanced power plan." }],
            });
        }

        if (hw.CpuIsHybrid && !hw.IsWindows11)
        {
            list.Add(new Tweak
            {
                Id = "hybrid-win10",
                Title = "Hybrid Intel CPU on Windows 10",
                Description = "Intel 12th-gen and newer CPUs mix P-cores and E-cores. Windows 10's scheduler doesn't fully understand them, so games can land on the slow E-cores. Windows 11's scheduler handles them properly.",
                Category = TweakCategory.System,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "Better 1% lows",
                Actions = [new ManualStepAction { Instructions = "Upgrade to Windows 11 for Thread Director support." }],
            });
        }

        if (hw.IsApu && gpu is not null)
        {
            var device = string.Join(' ', new[] { hw.SystemManufacturer, hw.SystemModel }.Where(x => x.Length > 0 && !x.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase)));
            var vendorApp = (hw.SystemManufacturer + hw.SystemModel + string.Join(' ', hw.Drives.Select(d => d.Model))).Contains("ONEX", StringComparison.OrdinalIgnoreCase)
                ? "OneXConsole" : "your device's control app (or AMD Software)";
            list.Add(new Tweak
            {
                Id = "apu-power",
                Title = $"{gpu.Name}: the CPU and GPU share one power budget",
                Description = $"On an APU{(device.Length > 0 ? $" ({device})" : "")}, FPS depends mostly on the total power limit (TDP) and on how much of it the GPU gets. " +
                              $"Game Mode leaves the Windows power plan alone here: forcing High performance raises CPU clocks and steals power from the GPU. Set the TDP in {vendorApp} instead. Your FPS cap also helps, because every frame you don't render is power the GPU can use to keep clocks steady.",
                Category = TweakCategory.Hardware,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "The biggest single factor on an APU",
                Actions = [new ManualStepAction { Instructions = $"{vendorApp}: choose the highest sustained TDP your cooling handles while plugged in (on Strix Halo devices this is typically the 'Performance'/'Turbo' mode), and keep it plugged in for gaming." }],
                Recommended = true,
            });

            var vram = gpu.VramGb;
            list.Add(new Tweak
            {
                Id = "apu-vram",
                Title = vram >= 8 ? $"{vram:0} GB of RAM reserved for graphics: good" : $"Only {vram:0.#} GB of RAM reserved for graphics",
                Description = vram >= 8
                    ? $"Your GPU uses part of system RAM as video memory. {vram:0} GB dedicated is a good balance for 1080p-1440p gaming (Windows sees the rest, {hw.RamGb:0} GB). Raise it only if games warn about VRAM; lowering it gives apps more RAM."
                    : "Your GPU uses part of system RAM as video memory, and games check that amount to pick texture settings. Raising it to 8-16 GB avoids stutter from textures spilling into shared memory.",
                Category = TweakCategory.Hardware,
                VisualImpact = VisualImpact.None,
                ExpectedGain = vram >= 8 ? "" : "Fewer VRAM stutters, higher texture settings",
                Actions = [new ManualStepAction { Instructions = "AMD Software → Performance → Tuning → Variable Graphics Memory (or BIOS → UMA Frame Buffer Size). Restart after changing." }],
                Recommended = vram < 8,
            });
        }

        if (gpu?.IsRdna3OrNewer == true)
        {
            list.Add(new Tweak
            {
                Id = "afmf",
                Title = "AMD Fluid Motion Frames 2 (driver frame generation)",
                Description = "AFMF 2 inserts a generated frame between real ones for any DX11/DX12/Vulkan game, without needing game support. From a steady 55-60 FPS base it shows 110-117 on your 120 Hz screen. It adds some input lag, so pair it with Anti-Lag. It works best with the in-game cap set to half your target (e.g. 58) and V-Sync off.",
                Category = TweakCategory.Graphics,
                VisualImpact = VisualImpact.Low,
                ExpectedGain = "Up to ~2x displayed FPS",
                Actions = [new ManualStepAction { Instructions = "AMD Software → Gaming → this game (or Global) → AMD Fluid Motion Frames → On (Search Mode: High; Performance Mode: Auto). Press Alt+R in-game to confirm it's active." }],
                Recommended = hw.IsApu,
            });
        }

        if (hw.VirtualDisplayAdapters.Count > 0)
        {
            list.Add(new Tweak
            {
                Id = "virtual-display",
                Title = $"Virtual display adapter: {string.Join(", ", hw.VirtualDisplayAdapters)}",
                Description = "Emulators and streaming tools add a virtual monitor. That's harmless while it's unused, but some games then open on the invisible screen, pick the wrong refresh rate, or lose FreeSync. If you see that, disable the adapter in Device Manager while gaming.",
                Category = TweakCategory.Info,
                VisualImpact = VisualImpact.None,
                Actions = [new ManualStepAction { Instructions = "Device Manager → Display adapters → right-click the virtual adapter → Disable device (re-enable it when you use the emulator)." }],
            });
        }

        if (gpu?.DriverDate is { } date && (DateTime.Now - date).TotalDays > 180)
        {
            list.Add(new Tweak
            {
                Id = "gpu-driver",
                Title = $"GPU driver is {(int)((DateTime.Now - date).TotalDays / 30)} months old",
                Description = "New drivers regularly add game-specific optimisations and fixes for shader stutter.",
                Category = TweakCategory.System,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "Varies; big for new releases",
                Actions = [new ManualStepAction { Instructions = gpu.Vendor switch
                {
                    GpuVendor.Nvidia => "Update through the NVIDIA App (Drivers tab).",
                    GpuVendor.Amd => "Update through AMD Software: Adrenalin Edition.",
                    GpuVendor.Intel => "Update through Intel Graphics Software / Driver & Support Assistant.",
                    _ => "Update your GPU driver.",
                } }],
                Recommended = true,
            });
        }

        if (gpu?.Vendor == GpuVendor.Nvidia)
        {
            list.Add(new Tweak
            {
                Id = "nv-shader-cache",
                Title = "Set the NVIDIA shader cache to Unlimited",
                Description = "The default shader cache can fill up and get evicted, so games recompile shaders and stutter again. Unlimited (or 10 GB+) keeps them.",
                Category = TweakCategory.Stutter,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "Fewer repeat shader stutters",
                Actions = [new ManualStepAction { Instructions = "NVIDIA Control Panel → Manage 3D settings → Shader Cache Size → Unlimited." }],
            });
        }

        if (!hw.RtssInstalled)
        {
            list.Add(new Tweak
            {
                Id = "install-rtss",
                Title = "Install RivaTuner Statistics Server (free) for perfect frame caps",
                Description = "RTSS has one of the most consistent frame-rate limiters and works with every graphics API. Once it's installed, the 90/100/110/117 targets are applied through it automatically, for any game.",
                Category = TweakCategory.FrameCap,
                VisualImpact = VisualImpact.None,
                ExpectedGain = "Flat frame times at your target",
                Actions = [new ManualStepAction { Instructions = "Download RTSS from guru3d.com, install it, and keep it running in the tray." }],
                Recommended = true,
            });
        }

        if (games is not null)
        {
            var hdd = hw.Drives.Where(d => d.Kind is DriveKind.Hdd or DriveKind.SdCard).Select(d => d.Letter).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var onHdd = games.Where(g => g.InstallDir.Length >= 2 && hdd.Contains(g.InstallDir[..2])).Select(g => g.Name).ToList();
            if (onHdd.Count > 0)
            {
                list.Add(new Tweak
                {
                    Id = "games-on-hdd",
                    Title = $"{onHdd.Count} game(s) on slow storage (hard drive or SD card)",
                    Description = "Moving these to an SSD will cut stutter and load times: " + string.Join(", ", onHdd.Take(8)) + (onHdd.Count > 8 ? "…" : ""),
                    Category = TweakCategory.Hardware,
                    VisualImpact = VisualImpact.None,
                    Actions = [new ManualStepAction { Instructions = "Move the folders to an SSD and update each game's path." }],
                    Recommended = true,
                });
            }
        }

        foreach (var d in hw.Drives.Where(d => d.TotalBytes > 0 && d.FreeBytes < d.TotalBytes * 0.08))
        {
            list.Add(new Tweak
            {
                Id = "low-disk-" + d.Letter,
                Title = $"Drive {d.Letter} is almost full",
                Description = "SSDs slow down noticeably when nearly full, and shader caches can't grow. Keep at least 10-15% free.",
                Category = TweakCategory.Hardware,
                VisualImpact = VisualImpact.None,
                Actions = [new ManualStepAction { Instructions = $"Free up space on {d.Letter}." }],
            });
        }

        return list;
    }

    /// <summary>Hardware-tailored Game Mode parameters.</summary>
    public static GameModeTuning Tune(HardwareProfile? hw)
    {
        var ramGb = hw?.RamGb ?? 16;
        return new GameModeTuning
        {
            // ISLC-style guard: purge the standby list when free memory drops below this.
            FreeMemoryThresholdMb = ramGb switch { < 12 => 2048, < 20 => 1536, < 40 => 1024, _ => 0 },
            StandbyListMinimumMb = 1024,
            // Don't switch to High Performance on X3D (breaks core parking), on battery, or on APUs:
            // there the CPU and GPU share one power budget, and pinning CPU clocks high starves the GPU.
            SwitchToHighPerformancePlan = !(hw?.CpuHasVCache ?? false) && !(hw?.OnBattery ?? false) && !(hw?.IsApu ?? false),
            // Windows 11 makes timer resolution requests per-process, so ours wouldn't reach the game.
            UseTimerResolution = !(hw?.IsWindows11 ?? true),
            TrimBackgroundWorkingSets = ramGb < 20,
            IsX3D = hw?.CpuHasVCache ?? false,
        };
    }
}

public sealed class GameModeTuning
{
    public int FreeMemoryThresholdMb { get; init; }
    public int StandbyListMinimumMb { get; init; }
    public bool SwitchToHighPerformancePlan { get; init; }
    public bool UseTimerResolution { get; init; }
    public bool TrimBackgroundWorkingSets { get; init; }
    public bool IsX3D { get; init; }
}
