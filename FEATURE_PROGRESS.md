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
- [x] RTSS caps, per-screen clamp, quick switch, 141 target. Verify: B2, `--filter OverlayTests.Includes141Target`
- [x] Thermal guard, idle cap, cooling modes, RyzenAdj power/undervolt with restore files. Verify: B1, B2; after a session, `ryzenadj --info` matches the values from before the session
- [x] Overlay layouts + watts (`OverlayFormatter`). Verify: `--filter OverlayTests`
- [x] Low-power sleep (`LowPowerSleep`). Verify: B1; on Windows, screen off → `powercfg /getactivescheme` overlay = efficiency, then restored on wake

### Views (`App/Views`, WPF)
- [x] MainWindow (tabs, borderless full screen Alt+Enter), Home, GameDetails, Mods, Storage, Hardware, Sessions, Settings, About, BigPicture, OverlayEditor. Verify: B1 (XAML compiles); `dotnet run --project src/FitzLauncher.App` on Windows opens with no binding errors in the debug output
- [x] Controller navigation (XInput + DualShock/DualSense HID). Verify: `--filter PlayStationTests`; on Windows, the D-pad moves focus on the Settings page
- [x] PS + button shortcuts (`PadComboEngine`, `WindowsActions`): volume, brightness, media, screenshot, Game Bar, etc.; PS tap still opens OneXConsole; chord buttons hidden from the game and menus. Verify: `--filter PadComboTests`; on device, PS + D-pad up shows the Windows volume OSD

## 2. Active work
- [x] **Smoothness mode removed**: still felt worse on device after the refresh-divisor fix; removed (UI, session logic, settings). Verify: B1; `grep -rn FpsStabiliser src` → nothing
- [x] **Controller profiles (ViGEm + HidHide)**: fixed in code, not yet confirmed on device. Cause: the remapper read only XInput slot 0 (built-in pad) while HidHide hid the DualShock. Now merges XInput + Sony HID (`PadMerge`). Verify: B1; on Windows, a slider drag changes `Library.json` → `Controller.LeftStick.InnerDeadzone`, and the pad works in a game with the profile on
- [x] **Optimise = session baseline** (`BaselinePicker`): best session on this screen at ≥ 95 % avg / ≥ 80 % 1% low → render scale + watts. Sessions page shows a Setup column. Verify: `--filter BaselineAndPowerTests`
- [x] **Graphics/resolution advisor** (`GraphicsAdvisor`): GPU-bound → in-game resolution / render scale; CPU-bound → CPU-heavy settings. Shown on the game page. Verify: `--filter BaselineAndPowerTests`
- [x] **Handheld wattage** (`PowerGovernor`, `Game.PowerByTarget`): Optimise turns on power control on APUs, W per target applied at launch, tuned live (+3/−2 W), learned minimum saved, original restored at exit. Not yet confirmed on device. Verify: `--filter BaselineAndPowerTests`; on Windows, `ryzenadj --info` before = after the session

- [x] Mods marketplace (Nexus): rankings (most downloaded / endorsed / popular this month / recently updated / newest), load more, per-mod compatibility check, one-click install with recursive requirements (Premium direct; free via Mod Manager Download pages), tracked mods + track installed + endorse, pre-launch check with auto-repair. Not yet confirmed on device. Verify: `--filter ModMarketTests`; on Windows, Find mods → Nexus → Install on a mod with requirements installs them first
- [x] Witcher 3 / Cyberpunk mod support: W3 mods/dlc placement, Script Merger conflict detection (mod0000_MergedFiles aware), unloaded folder names, pre-next-gen warning; CP2077 ArchiveXL/TweakXL/Codeware requirements, archive/pc/patch warning, pre-2.0 warning; free-account guided install queue. Not yet confirmed on device. Verify: `--filter GameSpecificModTests`
- [x] PS actions wheel (`PadComboEngine`, `ActionWheelWindow`): hold PS 0.35 s → wheel + legend of every PS + button shortcut; flick right stick, release PS to run; PS + button runs directly; quick tap still opens OneXConsole. While PS is held the game keeps buttons already held and never sees new presses or PS. Older View-button shortcuts are off by default. Not yet confirmed on device. Verify: `--filter ActionsWheelTests`; on device, hold PS over a borderless game and the wheel appears
- [x] Auto TDP for handhelds (`AutoTdpGovernor`, `GameSession`): Off / Watch only / On, global + per game, 25-60 W, 1% low ≥ 90 % of target = smooth; up fast (+5/+8 W) when dipping at the limit, down slowly (snap to draw, then −2 W per 40 s), remembers dip levels and big-scene floors, backs out when extra watts don't help, hands off during thermal guard; learned min/typical/max per game and target; per-session CSV in `auto-tdp/`; detects OneXConsole overriding the limit. Fan control not built (needs a verified Apex EC interface). Not yet confirmed on device. Verify: `--filter AutoTdpTests`; on device, run Watch only first and read the CSV
- [ ] Nexus SSO login (needs an application slug from Nexus support; personal API key used meanwhile)

## 3. Backlog
- [ ] Fluent layout contracts per `.claude/rules/windows-ui.md` (4 px spacing grid, 32 px minimum touch targets, `AutomationProperties.Name` on icon buttons). Verify: B1; `grep -rn 'Background="#' src/FitzLauncher.App/Views` → only preview mock-ups; Accessibility Insights FastPass shows 0 failures
- [ ] Process elevation controls: run the app as `asInvoker` with an elevated helper for RyzenAdj/powercfg/HidHide, and launch games un-elevated by default. Verify: B1; `app.manifest` level = `asInvoker`; power, cooling and controller features still work (session log has no "needs admin" lines)
- [ ] Dark/light theme tokens: `Theme.Light.xaml` with the same keys, a switch in Settings, follows the Windows app theme. Verify: B1; a script diffs the `x:Key` sets of both dictionaries → identical; toggling the theme needs no restart
- [ ] Settings persistence hardening: atomic write (temp + replace), schema version, migration tests. Verify: new `--filter SettingsPersistence*` (round-trip, a corrupt file falls back to the last snapshot), B2
- [ ] Debounce all slider-bound saves (Settings page binds POCO `S.*` directly). Verify: B1; dragging a slider writes `settings.json` ≤ 1× per 400 ms (watch with ProcMon)
