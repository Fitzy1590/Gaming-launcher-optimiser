# Feature progress

Checkpoint across sessions. Base checks `B` apply to every item:
- **B1** `dotnet build FitzLauncher.slnx` → 0 errors
- **B2** `dotnet test tests/FitzLauncher.Core.Tests` → 0 failed
- **B3** CI `build.yml` green on `main`, release `v0.3.N` published

UI is WPF only (no WinUI). The app manifest is `requireAdministrator`.

## 1. Implemented

### Game/launcher detection (`Core/Scanning`, `Core/GameUpdates`, `Core/Upscalers`)
- [x] Library folder scan + exe pick + name clean (`LibraryFolderScanner`, `ExeCandidateFinder`, `GameNameCleaner`). Verify: B1, B2, `--filter ScanningTests`
- [x] Engine/API/upscaler/anti-cheat scan (`GameScanner`, `BinaryFeatureScanner`, `PeImports`). Verify: B2, `--filter ScanningTests`
- [x] Official-source game update checks. Verify: B2, `--filter Batch4Tests`
- [x] Upscaler DLL updater + OptiScaler (anti-cheat games skipped). Verify: B1; on Windows, the game page lists the DLL versions after a swap

### Process priority / performance (`Windows/GameModeController`, `SessionHelpers`, `Core/Optimization`)
- [x] Game priority raise + background hog throttle to Idle, restored on exit (`CpuHogDetector`, `BackgroundLimiter`). Verify: B2, `--filter OptimizationTests`; on Windows, Task Manager shows the hog back at its original priority after the session
- [x] CPU affinity planning (`CpuAffinityPlanner`). Verify: B2, `--filter OptimizationTests`
- [x] RTSS caps, per-screen clamp, quick switch, 141 target. Verify: B2, `--filter OverlayAndStabiliserTests.Includes141Target`
- [x] Thermal guard, idle cap, cooling modes, RyzenAdj power/undervolt with restore files. Verify: B1, B2; after a session, `ryzenadj --info` matches the values from before the session
- [x] Overlay layouts + watts (`OverlayFormatter`). Verify: `--filter OverlayAndStabiliserTests`
- [x] Low-power sleep (`LowPowerSleep`). Verify: B1; on Windows, screen off → `powercfg /getactivescheme` overlay = efficiency, then restored on wake

### Views (`App/Views`, WPF)
- [x] MainWindow (tabs, borderless full screen Alt+Enter), Home, GameDetails, Mods, Storage, Hardware, Sessions, Settings, About, BigPicture, OverlayEditor. Verify: B1 (XAML compiles); `dotnet run --project src/FitzLauncher.App` on Windows opens with no binding errors in the debug output
- [x] Controller navigation (XInput + DualShock/DualSense HID). Verify: `--filter PlayStationTests`; on Windows, the D-pad moves focus on the Settings page

## 2. Active work
- [x] **Smoothness mode (`FpsStabiliser`)**: fixed in code, not yet confirmed on device. Cause: non-divisor caps (e.g. 100 on 120 Hz) judder without VRR. Now uses refresh divisors only unless Settings → "FreeSync / VRR" is on, and won't step up unless projected GPU load < 95 %. Verify: `--filter OverlayAndStabiliserTests`; on device, an A/B benchmark with it on shows p99 frametime ≤ with it off
- [x] **Controller profiles (ViGEm + HidHide)**: fixed in code, not yet confirmed on device. Cause: the remapper read only XInput slot 0 (built-in pad) while HidHide hid the DualShock. Now merges XInput + Sony HID (`PadMerge`). Verify: B1; on Windows, a slider drag changes `Library.json` → `Controller.LeftStick.InnerDeadzone`, and the pad works in a game with the profile on
- [ ] **Optimise = session baseline**: apply the best session's resolution/render scale when its FPS was ≥ 95 % of the cap. Verify: new Core test `--filter SessionBaseline*`, B2
- [ ] **Graphics/resolution advisor** to reach a stable target. Verify: new Core tests, B2
- [ ] **Handheld wattage advisor**: learn W per target, apply at launch/Optimise, restore after. Verify: new Core tests; on Windows, `ryzenadj --info` before = after the session

## 3. Backlog
- [ ] Fluent layout contracts per `.claude/rules/windows-ui.md` (4 px spacing grid, 32 px minimum touch targets, `AutomationProperties.Name` on icon buttons). Verify: B1; `grep -rn 'Background="#' src/FitzLauncher.App/Views` → only preview mock-ups; Accessibility Insights FastPass shows 0 failures
- [ ] Process elevation controls: run the app as `asInvoker` with an elevated helper for RyzenAdj/powercfg/HidHide, and launch games un-elevated by default. Verify: B1; `app.manifest` level = `asInvoker`; power, cooling and controller features still work (session log has no "needs admin" lines)
- [ ] Dark/light theme tokens: `Theme.Light.xaml` with the same keys, a switch in Settings, follows the Windows app theme. Verify: B1; a script diffs the `x:Key` sets of both dictionaries → identical; toggling the theme needs no restart
- [ ] Settings persistence hardening: atomic write (temp + replace), schema version, migration tests. Verify: new `--filter SettingsPersistence*` (round-trip, a corrupt file falls back to the last snapshot), B2
- [ ] Debounce all slider-bound saves (Settings page binds POCO `S.*` directly). Verify: B1; dragging a slider writes `settings.json` ≤ 1× per 400 ms (watch with ProcMon)
