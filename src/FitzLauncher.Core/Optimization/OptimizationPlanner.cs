using System.Globalization;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Scanning;

namespace FitzLauncher.Core.Optimization;

/// <summary>
/// Builds the per-game optimisation plan from the file scan, the hardware profile, the chosen FPS
/// target and (when available) the last recorded session. Nothing here touches the system.
/// </summary>
public sealed class OptimizationPlanner
{
    public const string FrameCapId = "framecap";
    private const string DxPrefsKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public OptimizationPlan Build(Game game, GameScanResult scan, HardwareProfile? hw, int? target, SessionSummary? last = null, IEnumerable<Tweak>? extra = null)
    {
        var plan = new OptimizationPlan { Target = target };
        var (suggested, why) = TargetAdvisor.Suggest(hw, last, target);
        plan.SuggestedTarget = suggested;
        plan.SuggestedTargetReason = why;

        var belowTarget = last is { HasFpsData: true } && target is > 0 &&
                          (last.TimeAtTarget < 0.92 || last.OnePercentLowFps < target * 0.9);
        var stuttery = last is { HasFpsData: true } && last.StutterCount > last.Duration.TotalMinutes * 6;

        AddFrameCap(plan, game, scan, hw, target);
        AddWindowsPerGame(plan, game, hw, scan);

        var isUnreal = scan.Engine is GameEngine.Unreal4 or GameEngine.Unreal5 or GameEngine.UnrealUnknownVersion;
        if (isUnreal) AddUnreal(plan, scan, hw, belowTarget, stuttery);
        if (scan.Engine == GameEngine.Unity) AddUnity(plan, scan);
        if (scan.Engine is GameEngine.Source or GameEngine.Source2) AddSource(plan, game);

        AddUpscalers(plan, scan, hw, belowTarget);
        AddInjectors(plan, scan);
        AddStorage(plan, game, hw);
        AddLatency(plan, scan, hw);
        AddSessionInsights(plan, last, target, hw);
        if (extra is not null) plan.Tweaks.AddRange(extra);

        foreach (var note in scan.Notes) plan.Notes.Add(note);
        return plan;
    }

    // ---------------------------------------------------------------- frame cap

    private static void AddFrameCap(OptimizationPlan plan, Game game, GameScanResult scan, HardwareProfile? hw, int? target)
    {
        if (target is not > 0) return;
        var t = target.Value;
        var refresh = hw?.PrimaryDisplay?.MaxRefreshHz ?? 0;
        var vrrNote = refresh > t
            ? $" Capping just under your {refresh} Hz refresh rate keeps frame pacing even and G-Sync/FreeSync active."
            : "";

        var actions = new List<TweakAction>();
        string method;
        if (hw is { RtssInstalled: true, RtssPath: not null })
        {
            // RTSS gives the most even frame pacing of the common limiters, for any graphics API.
            actions.Add(new RtssFrameLimitAction { RtssDirectory = hw.RtssPath, ExeName = RenderExeName(game, scan), Limit = t });
            method = "RivaTuner (RTSS) frame limiter";
        }
        else if (scan.Engine is GameEngine.Unreal4 or GameEngine.Unreal5 or GameEngine.UnrealUnknownVersion &&
                 UnrealConfigDir(scan) is { } dir)
        {
            actions.Add(new IniSetAction
            {
                FilePath = Path.Combine(dir, "GameUserSettings.ini"), Section = UnrealKeys.GameUserSettingsSection,
                Key = "FrameRateLimit", Value = t.ToString("0.000000", CultureInfo.InvariantCulture),
            });
            actions.Add(new IniSetAction { FilePath = Path.Combine(dir, "Engine.ini"), Section = "SystemSettings", Key = "t.MaxFPS", Value = t.ToString(CultureInfo.InvariantCulture) });
            method = "Unreal Engine's built-in limiter (Engine.ini + GameUserSettings.ini)";
        }
        else if (scan.Engine is GameEngine.Source or GameEngine.Source2)
        {
            actions.Add(new LaunchArgumentAction { Arguments = $"+fps_max {t}" });
            method = "the engine's fps_max launch option";
        }
        else
        {
            var vendorStep = hw?.PrimaryGpu?.Vendor switch
            {
                GpuVendor.Nvidia => $"NVIDIA Control Panel → Manage 3D settings → Program settings → {RenderExeName(game, scan)} → Max Frame Rate = {t}",
                GpuVendor.Amd => $"AMD Software → Gaming → {game.Name} → Frame Rate Target Control (or Radeon Chill min/max) = {t}",
                _ => $"Set the in-game frame limit to {t}",
            };
            actions.Add(new ManualStepAction { Instructions = $"{vendorStep}. Or install RivaTuner Statistics Server (free) and the launcher will set the cap automatically next time." });
            method = "your GPU driver's limiter (manual: this engine has no editable cap)";
        }

        plan.Tweaks.Add(new Tweak
        {
            Id = FrameCapId,
            Title = $"Lock to {t} FPS",
            Description = $"Caps frames at {t} using {method}. A steady cap gives smoother motion and lower input lag than a frame rate that keeps swinging, and it stops the GPU working flat out for frames you can't see, which keeps temperatures down.{vrrNote}",
            Category = TweakCategory.FrameCap,
            VisualImpact = VisualImpact.None,
            Risk = TweakRisk.Safe,
            ExpectedGain = "Stable frame times, lower temps",
            Actions = actions,
            Recommended = true,
        });
    }

    // ---------------------------------------------------------------- Windows per-game

    private static void AddWindowsPerGame(OptimizationPlan plan, Game game, HardwareProfile? hw, GameScanResult scan)
    {
        var hybridGraphics = hw is not null && hw.Gpus.Count(g => g.IsDiscrete) >= 1 && hw.Gpus.Any(g => !g.IsDiscrete);
        plan.Tweaks.Add(new Tweak
        {
            Id = "gpu-high-performance",
            Title = "Always use the high-performance GPU",
            Description = hybridGraphics
                ? "Your PC has both integrated and dedicated graphics. This forces the game onto the dedicated GPU. Without it, Windows sometimes picks the slow one."
                : "Sets Windows' per-app graphics preference to High performance for this game.",
            Category = TweakCategory.System,
            VisualImpact = VisualImpact.None,
            Risk = TweakRisk.Safe,
            ExpectedGain = hybridGraphics ? "Can be 2-5x FPS if it was on the iGPU" : "Guarantees the right GPU",
            Recommended = hybridGraphics,
            Actions = DxPrefs(game, scan, "GpuPreference", "2"),
        });

        if (hw?.IsWindows11 == true && !scan.UsesVulkan)
        {
            plan.Tweaks.Add(new Tweak
            {
                Id = "windowed-optimizations",
                Title = "Optimisations for windowed games",
                Description = "A Windows 11 feature that upgrades older DirectX 10/11 games running in borderless or windowed mode to the modern flip presentation model, giving lower latency and allowing VRR. DirectX 12 and Vulkan games already use it and are unaffected.",
                Category = TweakCategory.Latency,
                VisualImpact = VisualImpact.None,
                Risk = TweakRisk.Safe,
                ExpectedGain = "Lower input lag in borderless mode",
                Actions = DxPrefs(game, scan, "SwapEffectUpgradeEnable", "1"),
            });
        }
    }

    /// <summary>Exe name RTSS/driver profiles must use: the process that renders, not a bootstrap launcher.</summary>
    public static string RenderExeName(Game game, GameScanResult scan) => Path.GetFileName(scan.RenderExePath ?? game.ExePath);

    /// <summary>Windows keys these by full exe path; set both the launcher exe and the rendering exe.</summary>
    private static List<TweakAction> DxPrefs(Game game, GameScanResult scan, string name, string value) =>
        new[] { game.ExePath, scan.RenderExePath }
            .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (TweakAction)new RegistrySetAction
            {
                Hive = RegistryHive.CurrentUser, KeyPath = DxPrefsKey, ValueName = p!,
                Value = $"{name}={value};", ValueType = RegistryValueType.String, MergeSemicolonPairs = true,
            }).ToList();

    // ---------------------------------------------------------------- Unreal

    internal static string? UnrealConfigDir(GameScanResult scan) =>
        scan.ConfigDirectories.FirstOrDefault(d => d.Contains("Saved", StringComparison.OrdinalIgnoreCase));

    internal static int StreamingPoolMb(double vramGb) => vramGb switch
    {
        <= 0 => 0,
        < 5 => 1024,
        < 7 => 2048,
        < 9 => 3072,
        < 13 => 4096,
        < 17 => 6144,
        _ => 8192,
    };

    private static void AddUnreal(OptimizationPlan plan, GameScanResult scan, HardwareProfile? hw, bool belowTarget, bool stuttery)
    {
        var dir = UnrealConfigDir(scan);
        if (dir is null) return;
        var engineIni = Path.Combine(dir, "Engine.ini");
        var gusIni = Path.Combine(dir, "GameUserSettings.ini");
        IniSetAction Sys(string k, string v) => new() { FilePath = engineIni, Section = "SystemSettings", Key = k, Value = v };

        var stutterActions = new List<TweakAction>
        {
            Sys("r.ShaderPipelineCache.Enabled", "1"),
            Sys("r.CreateShadersOnLoad", "1"),
            Sys("D3D12.PSO.DiskCache", "1"),
            Sys("D3D12.PSO.DriverOptimizedDiskCache", "1"),
        };
        plan.Tweaks.Add(new Tweak
        {
            Id = "ue-shader-stutter",
            Title = "Reduce shader-compilation stutter",
            Description = "Turns on Unreal's pipeline cache, compiles shaders while levels load instead of mid-gameplay, and keeps the D3D12 shader disk cache on. Fixes the classic Unreal hitch the first time an effect appears.",
            Category = TweakCategory.Stutter,
            VisualImpact = VisualImpact.None,
            Risk = TweakRisk.Safe,
            ExpectedGain = "Fewer hitches; slightly longer loads",
            Actions = stutterActions,
            Recommended = true,
            Reason = stuttery ? "Your last session had frequent frame-time spikes." : null,
        });

        var vram = hw?.PrimaryGpu?.VramGb ?? 0;
        var pool = StreamingPoolMb(vram);
        if (pool > 0)
        {
            plan.Tweaks.Add(new Tweak
            {
                Id = "ue-texture-streaming",
                Title = $"Tune texture streaming for {vram:0} GB VRAM",
                Description = $"Sets the texture streaming pool to {pool} MB (sized for your GPU, capped to real VRAM) and turns on async loading. Textures stay resident instead of being streamed in and out, which removes pop-in and the hitches that come with traversal.",
                Category = TweakCategory.Stutter,
                VisualImpact = VisualImpact.None,
                Risk = TweakRisk.Moderate,
                ExpectedGain = "Smoother traversal, less pop-in",
                Actions =
                [
                    Sys("r.Streaming.PoolSize", pool.ToString(CultureInfo.InvariantCulture)),
                    Sys("r.Streaming.LimitPoolSizeToVRAM", "1"),
                    new IniSetAction { FilePath = engineIni, Section = UnrealKeys.StreamingSection, Key = "s.AsyncLoadingThreadEnabled", Value = "True" },
                ],
                Recommended = stuttery,
            });
        }

        plan.Tweaks.Add(new Tweak
        {
            Id = "ue-clean-image",
            Title = "Turn off motion blur, film grain and chromatic aberration",
            Description = "These post-process effects smear or add noise to the image. Many players prefer them off, and it gives back a few FPS.",
            Category = TweakCategory.Graphics,
            VisualImpact = VisualImpact.Low,
            Risk = TweakRisk.Safe,
            ExpectedGain = "+2-5% FPS, sharper motion",
            Actions = [Sys("r.MotionBlurQuality", "0"), Sys("r.SceneColorFringeQuality", "0"), Sys("r.Tonemapper.GrainQuantization", "0"), Sys("r.FilmGrain", "0")],
            Recommended = belowTarget,
        });

        // Scalability groups. Only step down from the very top tiers; never textures or resolution.
        var groups = new (string key, string label, VisualImpact impactFromCinematic, VisualImpact impactFromEpic, string gain)[]
        {
            ("sg.ShadowQuality", "Shadows", VisualImpact.Low, VisualImpact.Medium, "+5-15%"),
            ("sg.GlobalIlluminationQuality", "Global illumination (Lumen)", VisualImpact.Low, VisualImpact.Medium, "+10-20%"),
            ("sg.ReflectionQuality", "Reflections", VisualImpact.Low, VisualImpact.Medium, "+5-10%"),
            ("sg.PostProcessQuality", "Post-processing", VisualImpact.Low, VisualImpact.Low, "+3-8%"),
            ("sg.EffectsQuality", "Effects", VisualImpact.Low, VisualImpact.Low, "+3-8%"),
            ("sg.FoliageQuality", "Foliage", VisualImpact.Low, VisualImpact.Medium, "+3-10%"),
            ("sg.ShadingQuality", "Shading", VisualImpact.Low, VisualImpact.Medium, "+2-6%"),
        };
        foreach (var (key, label, fromCinematic, fromEpic, gain) in groups)
        {
            if (!scan.CurrentSettings.TryGetValue("GUS:" + key, out var raw) || !int.TryParse(raw, out var level) || level < 3) continue;
            var next = level - 1;
            var impact = level >= 4 ? fromCinematic : fromEpic;
            plan.Tweaks.Add(new Tweak
            {
                Id = "ue-" + key,
                Title = $"{label}: {LevelName(level)} → {LevelName(next)}",
                Description = level >= 4
                    ? $"Cinematic {label.ToLowerInvariant()} are meant for offline renders. Epic looks almost identical during play and is much cheaper."
                    : $"Epic → High {label.ToLowerInvariant()} is one of the best FPS-per-visual trades in Unreal games.",
                Category = TweakCategory.Graphics,
                VisualImpact = impact,
                Risk = TweakRisk.Safe,
                ExpectedGain = gain + " FPS",
                Actions = [new IniSetAction { FilePath = gusIni, Section = "ScalabilityGroups", Key = key, Value = next.ToString(CultureInfo.InvariantCulture), CreateIfMissing = false }],
                Recommended = belowTarget && (level >= 4 || impact <= VisualImpact.Low),
                Reason = belowTarget ? "Your last session was below the FPS target." : null,
            });
        }
    }

    private static string LevelName(int level) => level switch
    {
        0 => "Low", 1 => "Medium", 2 => "High", 3 => "Epic", >= 4 => "Cinematic", _ => level.ToString(),
    };

    // ---------------------------------------------------------------- Unity / Source

    private static void AddUnity(OptimizationPlan plan, GameScanResult scan)
    {
        var boot = scan.ConfigFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("boot.config", StringComparison.OrdinalIgnoreCase));
        if (boot is null) return;
        scan.CurrentSettings.TryGetValue("boot:gfx-enable-gfx-jobs", out var jobs);
        if (jobs == "1") return;
        plan.Tweaks.Add(new Tweak
        {
            Id = "unity-gfx-jobs",
            Title = "Multithreaded rendering (Unity graphics jobs)",
            Description = "Spreads Unity's render work across more CPU cores. It can lift CPU-bound FPS a lot, but some games crash or flicker with it. Revert it if that happens.",
            Category = TweakCategory.Stutter,
            VisualImpact = VisualImpact.None,
            Risk = TweakRisk.Experimental,
            ExpectedGain = "+0-25% in CPU-bound scenes",
            Actions =
            [
                new KeyValueFileSetAction { FilePath = boot, Key = "gfx-enable-gfx-jobs", Value = "1" },
                new KeyValueFileSetAction { FilePath = boot, Key = "gfx-enable-native-gfx-jobs", Value = "1" },
            ],
        });
    }

    private static void AddSource(OptimizationPlan plan, Game game)
    {
        if (game.Arguments.Contains("-novid", StringComparison.OrdinalIgnoreCase)) return;
        plan.Tweaks.Add(new Tweak
        {
            Id = "source-novid",
            Title = "Skip intro videos",
            Description = "Adds -novid so the game boots straight to the menu.",
            Category = TweakCategory.Info,
            VisualImpact = VisualImpact.None,
            Risk = TweakRisk.Safe,
            ExpectedGain = "Faster start-up",
            Actions = [new LaunchArgumentAction { Arguments = "-novid" }],
        });
    }

    // ---------------------------------------------------------------- upscalers / injectors / storage

    private static void AddUpscalers(OptimizationPlan plan, GameScanResult scan, HardwareProfile? hw, bool belowTarget)
    {
        var gpu = hw?.PrimaryGpu;
        var isRtx = gpu?.Vendor == GpuVendor.Nvidia && gpu.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase);
        if (scan.Upscalers.Contains(Upscaler.Dlss) && (isRtx || gpu is null))
        {
            plan.Tweaks.Add(new Tweak
            {
                Id = "dlss-quality",
                Title = "Use DLSS Quality (or DLAA if you have headroom)",
                Description = "This game ships DLSS. DLSS Quality renders internally at a lower resolution and reconstructs to your full output resolution. At 1440p and 4K, the modern transformer model often looks as good as native TAA. Set it in the game's graphics menu.",
                Category = TweakCategory.Graphics,
                VisualImpact = VisualImpact.Low,
                Risk = TweakRisk.Safe,
                ExpectedGain = "+30-60% FPS",
                Actions = [new ManualStepAction { Instructions = "In-game: Graphics → Upscaling → DLSS → Quality." }],
                Recommended = belowTarget,
            });
            if (scan.DlssVersion is { } v && Version.TryParse(v, out var ver) && ver.Major < 3)
            {
                plan.Tweaks.Add(new Tweak
                {
                    Id = "dlss-update",
                    Title = $"Upgrade DLSS ({v} is old)",
                    Description = "This game bundles an old DLSS version. Turn on the NVIDIA App's DLSS Override (Model presets → Latest) to use the newest model, which gives sharper output at the same performance.",
                    Category = TweakCategory.Graphics,
                    VisualImpact = VisualImpact.None,
                    Risk = TweakRisk.Safe,
                    ExpectedGain = "Better image at the same FPS",
                    Actions = [new ManualStepAction { Instructions = "NVIDIA App → Graphics → Program settings → this game → DLSS Override – Model presets → Latest." }],
                });
            }
        }
        else if (scan.Upscalers.Contains(Upscaler.Fsr) || scan.Upscalers.Contains(Upscaler.Xess))
        {
            var name = scan.Upscalers.Contains(Upscaler.Xess) && gpu?.Vendor == GpuVendor.Intel ? "XeSS" : "FSR";
            plan.Tweaks.Add(new Tweak
            {
                Id = "fsr-quality",
                Title = $"Use {name} Quality",
                Description = $"This game supports {name}. Quality mode keeps your output resolution and renders internally a little lower.",
                Category = TweakCategory.Graphics,
                VisualImpact = VisualImpact.Medium,
                Risk = TweakRisk.Safe,
                ExpectedGain = "+25-45% FPS",
                Actions = [new ManualStepAction { Instructions = $"In-game: Graphics → Upscaling → {name} → Quality." }],
                Recommended = belowTarget,
            });
        }

        if (scan.Upscalers.Contains(Upscaler.DlssFrameGen) && gpu?.Name is { } n && (n.Contains("RTX 40") || n.Contains("RTX 50")))
        {
            plan.Tweaks.Add(new Tweak
            {
                Id = "dlss-fg",
                Title = "DLSS Frame Generation is available",
                Description = "Frame generation can lift a 60-70 FPS base to well over 100 FPS, at the cost of some extra input latency. It works best when the base frame rate is already 60+. It needs Hardware-accelerated GPU scheduling turned on.",
                Category = TweakCategory.Graphics,
                VisualImpact = VisualImpact.Low,
                Risk = TweakRisk.Safe,
                ExpectedGain = "Up to ~1.8x displayed FPS",
                Actions = [new ManualStepAction { Instructions = "In-game: enable DLSS Frame Generation (and NVIDIA Reflex)." }],
                Recommended = belowTarget,
            });
        }
    }

    private static void AddInjectors(OptimizationPlan plan, GameScanResult scan)
    {
        foreach (var inj in scan.Injectors.Where(i => i is "ReShade" or "ENB"))
        {
            plan.Tweaks.Add(new Tweak
            {
                Id = "injector-" + inj.ToLowerInvariant(),
                Title = $"{inj} is installed",
                Description = $"{inj} post-processing can cost 5-30% FPS depending on the shaders. If you're short of your target, disable the heavy effects (ambient occlusion, depth of field, ray-traced GI).",
                Category = TweakCategory.Info,
                VisualImpact = VisualImpact.Low,
                Risk = TweakRisk.Safe,
                Actions = [new ManualStepAction { Instructions = $"Open the {inj} overlay in-game and turn off the expensive effects." }],
            });
        }
    }

    private static void AddStorage(OptimizationPlan plan, Game game, HardwareProfile? hw)
    {
        if (hw is null || game.InstallDir.Length < 2) return;
        var letter = game.InstallDir[..2].ToUpperInvariant();
        var drive = hw.Drives.FirstOrDefault(d => d.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase));
        if (drive?.Kind != DriveKind.Hdd) return;
        plan.Tweaks.Add(new Tweak
        {
            Id = "move-to-ssd",
            Title = "This game is on a hard drive",
            Description = $"{letter} is a spinning hard drive. Modern games stream assets constantly, and from an HDD that shows up as stutter and texture pop-in no setting can fix. Moving the game to an SSD is the single biggest fix for that.",
            Category = TweakCategory.Hardware,
            VisualImpact = VisualImpact.None,
            Risk = TweakRisk.Safe,
            ExpectedGain = "Much smoother traversal and faster loads",
            Actions = [new ManualStepAction { Instructions = "Move the game folder to an SSD, then update the path here (Edit game)." }],
            Recommended = true,
        });
    }

    // ---------------------------------------------------------------- latency (Reflex / Anti-Lag)

    private static void AddLatency(OptimizationPlan plan, GameScanResult scan, HardwareProfile? hw)
    {
        var vendor = hw?.PrimaryGpu?.Vendor ?? GpuVendor.Unknown;
        if (vendor == GpuVendor.Nvidia)
        {
            plan.Tweaks.Add(scan.SupportsReflex
                ? Latency("latency-reflex", "Turn on NVIDIA Reflex (On + Boost)",
                    "This game has NVIDIA Reflex built in. It keeps the CPU from queueing frames ahead of the GPU, which noticeably cuts input lag. With G-Sync and V-Sync on, Reflex also caps FPS just under your refresh rate by itself; your RTSS cap stays as a ceiling.",
                    "In-game: Graphics → NVIDIA Reflex Low Latency → On + Boost.", "Up to ~30-50% lower latency", true)
                : Latency("latency-nv-llm", "Use NVIDIA Low Latency Mode",
                    "This game has no Reflex, so use the driver's version: Low Latency Mode = On limits the render queue to one frame. Avoid 'Ultra' together with an RTSS cap, because Ultra sets its own cap.",
                    "NVIDIA Control Panel → Manage 3D settings → Program settings → this game → Low Latency Mode → On.", "Lower input lag", false));
            if (hw?.RtssInstalled == true)
                plan.Tweaks.Add(Latency("latency-rtss-mode-nv", "Set RTSS's limiter to NVIDIA Reflex mode",
                    "RTSS 7.3.4+ can cap frames using Reflex's own sleep, which community latency tests rank lowest of RTSS's limiter modes. It's a global RTSS setting, so the launcher leaves the switch to you and doesn't change your RTSS/Afterburner setup.",
                    "RTSS → Setup → Framerate limiter → NVIDIA Reflex.", "Lower latency at the same cap", false));
        }
        else if (vendor == GpuVendor.Amd)
        {
            plan.Tweaks.Add(scan.SupportsAntiLag2
                ? Latency("latency-antilag2", "Turn on AMD Anti-Lag 2",
                    "This game has AMD Anti-Lag 2 built in: AMD's equivalent of NVIDIA Reflex. It syncs the CPU to the GPU in-engine for the lowest input lag on Radeon.",
                    "In-game: Graphics → AMD Anti-Lag 2 → On.", "Up to ~30-40% lower latency", true)
                : Latency("latency-antilag", "Turn on Radeon Anti-Lag (driver)",
                    "This game has no Anti-Lag 2, so use the driver-level Anti-Lag in AMD Software for this game. It's per game and safe for single-player repacks.",
                    "AMD Software → Gaming → this game → Radeon Anti-Lag → Enabled.", "Lower input lag", false));
            if (hw?.RtssInstalled == true)
                plan.Tweaks.Add(Latency("latency-rtss-mode-amd", "Pick RTSS's limiter mode for Radeon",
                    "RTSS's Reflex mode is NVIDIA-only. On Radeon, 'Async' gives the lowest latency and 'Front edge sync' the most even frame pacing. For the steady 90-117 FPS you're after, front edge sync is the pick. It's a global RTSS setting, so the launcher leaves it to you.",
                    "RTSS → Setup → Framerate limiter → Front edge sync (smoothest) or Async (lowest latency).", "Smoother pacing at the cap", false));
        }
    }

    private static Tweak Latency(string id, string title, string description, string step, string gain, bool recommended) => new()
    {
        Id = id, Title = title, Description = description, Category = TweakCategory.Latency,
        VisualImpact = VisualImpact.None, Risk = TweakRisk.Safe, ExpectedGain = gain, Recommended = recommended,
        Actions = [new ManualStepAction { Instructions = step }],
    };

    private static void AddSessionInsights(OptimizationPlan plan, SessionSummary? last, int? target, HardwareProfile? hw)
    {
        if (last is not { HasFpsData: true }) return;
        if (target is > 0 && last.AverageFps >= target * 0.98 && last.OnePercentLowFps < target * 0.75)
            plan.Notes.Add($"Your average FPS ({last.AverageFps:0}) reaches the target, but the 1% lows ({last.OnePercentLowFps:0}) don't. That points to stutter rather than a lack of GPU power, so the stutter fixes will help more than lowering settings.");
        if (last.MaxGpuTempC is > 87)
            plan.Notes.Add($"The GPU reached {last.MaxGpuTempC:0} °C last session. A lower FPS target or a more aggressive fan curve will stop it thermal-throttling.");
        if (last.MaxCpuTempC is > 95)
            plan.Notes.Add($"The CPU reached {last.MaxCpuTempC:0} °C. Check the cooler mounting and case airflow; laptops benefit from a cooling pad.");
        if (hw?.PrimaryDisplay is { } d && target is > 0 && d.MaxRefreshHz > 0 && target > d.MaxRefreshHz)
            plan.Notes.Add($"The target ({target}) is above your display's {d.MaxRefreshHz} Hz, so frames beyond that are never shown.");
    }
}
