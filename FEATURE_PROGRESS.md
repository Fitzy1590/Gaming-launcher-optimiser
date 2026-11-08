# Feature progress

Checkpoint for work across sessions. Update it when a feature changes state. Status values: **done** (shipped), **wip** (in progress), **todo** (not started), **issue** (shipped but reported broken).

## Launcher and game detection (`Core/Scanning`, `Core/Runtimes`)
| Module | Status | Notes |
|---|---|---|
| Library folder scan (`LibraryFolderScanner`, `ExeCandidateFinder`, `GameNameCleaner`) | done | Repack folder layouts, picks the main exe, cleans names |
| Game scan (`GameScanner`, `BinaryFeatureScanner`, `PeImports`) | done | Engine, APIs, upscalers, anti-cheat |
| Runtime/redist detection (`RedistService`) | done | |
| Game updates from official sources (`Core/GameUpdates`) | done | Official sources only, no repack sites |
| Mods: Nexus/Thunderstore/mod.io, FOMOD, update alerts | done | |
| Upscaler DLL updater + OptiScaler (`Core/Upscalers`) | done | Daily swap, anti-cheat games skipped |

## Process and performance optimisation (`Core/Optimization`, `Windows`)
| Module | Status | Notes |
|---|---|---|
| Game Mode: priority, background limiter (`GameModeController`, `CpuHogDetector`) | done | Restored on exit |
| CPU pinning (`CpuAffinityPlanner`) | done | |
| FPS caps via RTSS, per-screen caps, quick switch, 141 target | done | |
| Thermal guard, idle cap, cooling modes | done | |
| APU power limit + undervolt (RyzenAdj) | done | |
| Smoothness mode (`FpsStabiliser`) | issue | User reports games *feel* less smooth with it on |
| Per-game overlay layouts + watts (`OverlayFormatter`) | done | |
| Low-power sleep (`LowPowerSleep`) | done | Not yet confirmed on device |
| Optimise applies the best known session settings (resolution/render scale) as a baseline | todo | Requested |
| In-game graphics settings and resolution suggestions to reach the target FPS | todo | Requested |
| Handheld wattage advisor: learn watts per target, apply at launch/optimise, restore after | todo | Requested; partly exists (per-game power learning) |

## Controllers (`Windows/Controllers`, `PlayStationPads`, `ConsoleBridge`, App `GamepadNavigator`)
| Module | Status | Notes |
|---|---|---|
| Menu navigation (XInput + Sony HID) | done | Spatial focus moves |
| Per-game profiles (ViGEm + HidHide) | issue | DualShock stops working when on; deadzone sliders still not editable |
| PS button → OneXConsole + pad mirroring | done | Not yet confirmed on device |

## UI views (`App/Views`)
| View | Status | Notes |
|---|---|---|
| MainWindow (tabs, full screen) | done | |
| Home, Library/GameDetails, Mods, Storage, Hardware, Sessions, Settings, About | done | |
| BigPictureWindow | done | |
| ControllerProfileEditor | issue | See controllers |
| OverlayEditor | done | |
| Theme tokens (`Theme.xaml`) | done | Scrollbars and sliders reworked |

## Next up
1. Fix the controller profile (DualShock + HidHide, slider editing).
2. Investigate smoothness mode feel (RTSS limit changes, limiter mode), and pull it if it can't be fixed.
3. Optimise applies the session baseline, plus graphics/resolution suggestions.
4. Handheld wattage advisor with auto-apply and revert.
