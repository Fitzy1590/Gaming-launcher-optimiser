# Gaming-launcher-optimiser — Fitz Launcher

A fast, clean Windows game launcher for all your games and repacks, with one-click optimisation for
**stable 90 / 100 / 110 / 117 FPS**, an automatic **Game Mode**, a low-cost **FPS tracker** and
**temperature alerts**, all tailored to the PC it runs on.

> **Status: v0.3.** It compiles with zero warnings, and the core logic has 82 passing unit tests. It has
> **not been run on a real Windows PC yet** (it was built in a Linux container). Expect a first round
> of fixes once you try it. See [Testing checklist](#testing-checklist). Ideas to vote on: [docs/IDEAS.md](docs/IDEAS.md).

## Features

### Library
- **Scan game folders:** pick one or more folders where your games are installed, then review every game found
  before anything is added. You can tick or untick each one, rename it, and choose which exe it launches (click to cycle
  through the candidates, or browse). Each game is flagged *New*, *Already in library*, *Not installed* (a FitGirl/DODI
  `setup.exe` + `.bin` installer that hasn't been run yet), *No exe found* or *on SD card*. The folders are remembered:
  scanning again, and the check at start-up, show only new games.
- **Add one game:** pick an `.exe` or shortcut, or **drag & drop** exes, shortcuts or folders onto the window.
- Repack/scene folder names are cleaned automatically (`Elden.Ring.v1.10-[FitGirl Repack]` → *Elden Ring*).
  The installer, redistributable and crash-reporter exes are ignored when the launcher picks the game exe.
- **Artwork** is fetched automatically: portrait cover plus a wide hero image from Steam's store (no key needed).
  Add a free SteamGridDB API key in Settings to cover non-Steam games. You can also pick your own image.
- Instant search (Ctrl+F), Enter to play, double-click a cover to play, Ctrl+1..4 to switch pages.
  Covers are decoded at display size off the UI thread, so scrolling stays smooth.

### One-click optimise (per game)
Click **90**, **100**, **110** or **117** (or **⚡ Optimise**). The launcher then:
1. **Scans every file** of the install to detect the engine (Unreal 4/5, Unity, Source, RE Engine, CryEngine…),
   DLSS / FSR / XeSS and frame generation, ReShade/ENB, DirectX 12 / Vulkan, and where the game's config lives.
2. Applies **only changes with no visual cost**:
   - **Frame cap** at your target. It uses RivaTuner (RTSS) if installed (the most even pacing, any API),
     otherwise Unreal's own `FrameRateLimit`/`t.MaxFPS` or Source's `fps_max`, otherwise it gives exact
     NVIDIA/AMD control-panel steps.
   - Forces the **high-performance GPU** (this matters a lot on laptops with two GPUs).
   - Windows 11 **optimisations for windowed games** (flip model: lower latency in borderless mode).
   - Unreal: **shader-stutter fixes** (pipeline cache, compile on load, D3D12 PSO cache) and
     **texture-streaming pool sized to your VRAM**.
3. **Graphics suggestions** are listed separately, with their visual impact and expected FPS gain:
   shadows/GI/reflections Cinematic→Epic, DLSS/FSR Quality, motion blur off, Unity graphics jobs…
   They're flagged **Recommended** when your recorded FPS shows you missed the target, and
   **Apply recommended** applies them. Texture quality and resolution are never touched.
4. **Everything is revertible.** Original values are recorded, config files are backed up before the
   first edit, and **Revert all** puts the game back exactly as it was.

The launcher also **suggests a target**: 117 on a 120 Hz screen (just under refresh, so G-Sync/FreeSync stay
engaged), or after you've played, the highest target your 1% lows can actually hold.

### Game Mode (automatic when you press Play)
- High-performance power plan, or "Best performance" power mode on laptops. It's skipped on AMD X3D CPUs,
  which need Balanced for core parking, and on battery.
- **RAM clear:** standby-list purge before launch, plus an ISLC-style **memory guard** during play. The
  thresholds are tuned to your RAM size, and low-RAM PCs also get background working sets trimmed.
- **Pauses Windows Update, BITS and Delivery Optimisation downloads, search indexing and telemetry**, closes
  updaters, and pauses OneDrive sync.
- Game at **High priority**; browsers and other background apps at **below normal**.
- 0.5 ms timer resolution on Windows 10. Windows 11 makes timer requests per-process, so it's skipped there.
- **All of it is restored** when the game exits. Every step is written to a restore file first, so if the PC
  crashes mid-game, the next launcher start puts everything back.
- The launcher itself minimises and frees its own memory while you play.
- Every option and list is editable in **Settings**.

### FPS tracker (low cost)
Uses the same Windows ETW events as Intel's PresentMon: no overlay, no DLL injection (anti-cheat safe).
Frame times go into a fixed 20 KB histogram, so cost doesn't grow with session length. After each session you
get **average FPS, 1% and 0.1% lows, % of frames on target, stutter count**, max/average CPU and GPU temps, and an
FPS-over-time chart. Session history is kept per game and on the **FPS history** page.

### Temperatures
Live CPU/GPU temperature, load and power on the **Hardware & temps** page, via LibreHardwareMonitor. While
you play, an **alert** fires if the CPU or GPU stays above your limit (default 90 °C / 85 °C) for 10 s. It shows a
click-through overlay that never steals focus, and plays a sound that also works in exclusive fullscreen.

### Performance automation
- **Auto-tune:** after a session that misses your target, it applies *one* graphics suggestion (least visible
  first). The next session decides: keep it if the 1% lows improved by 3% or more, otherwise revert it. This repeats
  until the target holds. The full history (✓ kept / ↶ reverted) is shown per game.
- **CPU core pinning:** keeps the game on the **V-Cache CCD** on dual-CCD Ryzen X3D chips (7950X3D/9950X3D), or on
  **P-cores** on Intel 12th gen+. It reads the real cache and core topology from Windows. Auto by default and
  per-game overridable; single-CCD and non-hybrid CPUs are left alone.
- **Thermal guard:** if the CPU or GPU sits near its limit, it lowers the RTSS cap a step (117 → 110 → 100 → 90…)
  live, without restarting the game, and puts it back once things cool down.
- **Low latency, NVIDIA *and* AMD:** it detects **NVIDIA Reflex** and **AMD Anti-Lag 2** inside the game files and
  tells you to turn them on. Otherwise it points you to the driver-level option (NVIDIA Low Latency Mode / Radeon
  Anti-Lag), plus the best RTSS limiter mode for your GPU (Reflex mode on NVIDIA, front-edge sync on AMD).
- **Benchmarks:** press **Ctrl+Alt+B** in-game to record a 60-second run (avg, 1% and 0.1% lows, % on target).
  Runs are listed per game with the change versus the previous run.
- **Per-game presets:** known config fixes from `presets.json` in the data folder show up as normal, revertible
  suggestions. None are built in yet; I'd rather not ship unverified fixes.
- **DLSS / XeSS / FSR 3.1 DLL updates:** the newest copy of each DLL from your *other* games (or a folder of DLLs you
  downloaded) is offered as a backed-up, revertible swap. Nothing is downloaded.
- **Shader-cache cleaner:** shows DirectX, NVIDIA, AMD and Intel shader cache sizes and clears them. After a GPU
  driver update the launcher suggests it automatically.

### RTSS & MSI Afterburner, without conflicts
- Frame caps go into the **per-game RTSS profile only** (just the limit). RTSS's global settings are never touched.
- The overlay line (`FITZ | cap 117 | 1% low 104 | GPU 71C CPU 64C`) uses **its own RTSS OSD slot**. Afterburner's
  lines are untouched, and ours is removed when the game closes.
- When Afterburner is running, its sensor readings are used **instead of polling the sensors a second time**.
  LibreHardwareMonitor fills in only what Afterburner doesn't provide.

### Repack tools
- **Save backups** before and after every session, made only when something changed. Saves are found
  automatically in the Steam-emulator folders repacks use (Goldberg, CODEX, RUNE, TENOKE, EMPRESS, OnlineFix…, via the
  repack's `steam_appid.txt`), in Unreal/Unity save locations, and in folders named after the game. You can add or
  remove folders. The last 10 backups are kept, and one-click restore backs up the current saves first.
- **Extra copies to Google Drive and USB.** Google Drive uses the Google Drive for desktop "My Drive" folder (no
  sign-in inside the launcher). USB drives are recognised even if the drive letter changes, and backups made while a
  drive was unplugged are copied over the next time it's connected. Any other folder (NAS, OneDrive) works too.
- **Runtimes check:** "Won't start?" finds the VC++, DirectX, .NET, PhysX, OpenAL and XNA installers bundled in the
  repack, checks which are missing on your PC, and installs them silently.

### Home & Storage
- **Home:** continue playing (with Play buttons), this week's hours and % of frames on target, things that need
  attention (games on the SD card, missing cover art or save folders, an available update, a nearly full drive),
  and recently added games.
- **Storage:** drive usage and games sorted by size, with:
  - **Move…** — copies the game to another drive (e.g. SD card → NVMe), verifies every file, re-points the exe,
    saves, edited config and swapped DLLs, and only then deletes the old copy.
  - **Verify** — checks files against the repack's MD5/SFV lists, or against a saved baseline, to catch
    missing or corrupt files (e.g. removed by antivirus).
  - **Uninstall** — backs up saves, reverts the launcher's changes, sends the folder to the Recycle Bin and
    removes the game from the library.

### More automation
- **Per-game power limits (Ryzen APUs, via RyzenAdj):** starts each game at a set wattage, lowers it a step
  after sessions that hit the target easily and raises it after misses, so you get the same FPS at less heat and
  noise. The original limits are restored afterwards, and again after a crash.
- **In-game quick switch:** hold View and press D-pad up/down (or Ctrl+Alt+Up/Down) to change the FPS target while
  playing. It applies instantly through RTSS, shows in the overlay, and is kept for that game.
- **RTSS limiter A/B:** compares async and front-edge-sync limiting over two sessions and keeps the smoother one
  (only when your RTSS version stores the mode in its profiles).
- **"Why is it hot?"** — heat alerts explain whether the chip is working flat out (lower the cap or power) or is
  hot at low load (cooling: dust, vents, room, fan curve).
- **Achievement progress for repacks:** read from Goldberg/GSE and CODEX/RUNE files. The total comes from the
  repack or Steam, and it feeds the progress bar.
- **ReShade/ENB on/off** per game: renames only the mod's own DLL (never DXVK or loaders).
- **Sync library and settings** through Google Drive for desktop. Newer data from another PC is offered at start-up.
- **Verify a repack download** before installing (the *Not installed* rows in Scan game folders).

### Advanced (batch 4)
- **Core parking off while playing:** keeps every CPU core awake so the game never waits for one to wake. Skipped on
  X3D CPUs (their V-Cache scheduling relies on parking) and restored afterwards.
- **Background CPU limiter:** any background app that uses a lot of CPU for two checks in a row is set to the lowest
  priority until it calms down (Windows processes, RTSS and Afterburner are never touched).
- **Adaptive cap:** with no keyboard, mouse or controller input for 12 s (menus, maps, cutscenes) the cap drops to
  60 FPS to save heat; any input puts it straight back.
- **Quick menu:** hold View + LB to show target, power and temps in the overlay; D-pad up/down changes the target,
  left/right changes the power limit by 5 W.
- **Sleep and resume where you left off:** press the power button (or let the screen time out) and the game is
  frozen: no CPU, sound or heat. Wake the device and sign in, and it unfreezes about a second later with the FPS cap,
  power limit and undervolt put back, and the game window brought to the front. Works with handheld Modern Standby
  (screen off) and desktop sleep. Sleep time isn't counted as play time. If the launcher is closed or crashes, the game is
  unfrozen straight away (or at the next start). Per-game off switch for online games.
- **Idle sleep during games:** many games keep the screen on forever; the launcher overrides that for the game while it
  runs (restored afterwards), so Windows' own screen-off/sleep timers work, and controller input counts as activity.
- **DXVK for old DX9/DX11 games:** offered as a suggestion; the official release is downloaded once from GitHub and
  copied next to the game. Skipped when ReShade/ENB, DX12 or Vulkan is in use. Revertible.
- **Ryzen undervolt (advanced, off by default):** Curve Optimizer through RyzenAdj, only while playing. Starts at -5,
  steps 5 further after each clean session up to your limit, and backs off a step for good after any crash or freeze
  (also detected at the next start-up if the PC froze).
- **Crash watcher + Roll back:** a crash exit code or a Windows "Application Error" event marks the session as
  crashed, names the module that failed, and offers to undo every change made since the last clean session.
- **Report card:** an A–F grade per game from recent sessions (smoothness, temperatures, crashes) and the one change
  most likely to help. The grade also shows on the library card.
- Not built yet: setting AMD driver features (Anti-Lag, Chill, RSR) automatically. It needs AMD's ADLX library,
  which can't be tested without the hardware; it will come as a separate update.

### Mods
Each game has a **Mods** window (also the Mods tab), styled around the game's art:
- **Find mods** on **Nexus Mods** (your free API key; free accounts install through the site's *Mod Manager Download*
  button, which the launcher can handle, Premium installs directly), **Thunderstore** (no account; dependencies such as
  BepInExPack install automatically) and **mod.io** (free API key).
- **Installs to the right place** for the game: Bethesda `Data` (plugins activated in Plugins.txt), Unreal `~mods`,
  UE4SS mods, BepInEx plugins, MelonLoader, Cyberpunk archive/redscript/CET folders, ASI/proxy DLLs next to the exe, or
  archives that already mirror the game's folders. FOMOD installers use the author's recommended options. Zip, 7z and rar.
- **On/off, order and remove** per mod. Files go through a staging folder; anything a mod overwrites is backed up and put
  back when it's turned off. *Remove all* returns the game to its original files.
- **Compatibility checker:** missing mod loaders, script extender vs game version (e.g. SKSE built for another runtime),
  game updated since install, 32/64-bit DLLs, missing Bethesda masters, missing dependencies, DLL clashes with ReShade,
  DXVK or ASI loaders, IoStore pak mods, and which mod wins each overlap. Anti-cheat warning for online games.
- **Troubleshooter:** explains the last crash's exit code, names the mod that owns the crashing DLL, reads SKSE/F4SE,
  Crash Logger/Trainwreck, BepInEx, MelonLoader, UE4SS, CET and Unity logs, and ranks the suspects. **Find the bad mod**
  halves the suspects each session; **Copy error report** makes a shareable report with your user name hidden.

### Trainers
**FLiNG trainers** per game: search flingtrainer.com from the game page, see which download matches your game version,
download it into the launcher's trainers folder (or use one you downloaded), and it opens a set time after the game
starts and closes with it. Tick your usual cheats and their hotkeys show in the overlay when it opens. Antivirus apps
often flag trainers; the launcher never changes antivirus settings and tells you what happened.

### Insights and AMD features
- **Stutter detective**: after each session, every stutter is matched to what the PC was doing at that moment (shader
  compiling, a background app's CPU spike, memory nearly full, overheating, CPU maxed out, or uneven frame pacing). The
  game page shows the main cause, the fix and the worst moments.
- **A/B test**: compare two cooling modes, two FPS caps, or anything you change yourself. Each run records a benchmark
  automatically after a warm-up; the verdict weighs smoothness, heat, FPS and stutters, and **Keep the winner** applies it.
- **AMD features per game** (AMD GPUs): Anti-Lag, Radeon Chill (with an FPS range), Radeon Super Resolution, Fluid Motion
  Frames, Image Sharpening and Radeon Boost, each Leave / On / Off. Applied through AMD's ADLX library when the game
  starts and put back afterwards (and at the next start after a crash). "What's supported?" asks the driver.
- **Big Picture options** (Start): cooling, FPS cap for this screen, controller settings, trainer, mods, game update and
  stutter report, all with the controller.

### Mods extras, snapshots and safe mode
- **FOMOD option picker** for mods with install choices (recommended options pre-ticked).
- **Mod update alerts**: the Mods window checks Nexus, Thunderstore and mod.io; "Update to vX" keeps the mod's order and
  its off state.
- **Library snapshots**: a daily copy of your library, settings, mod/trainer/controller setups and history (14 kept),
  restorable from Settings.
- **Safe mode**: two crashes in a row while starting opens the launcher with extras off, with one button to turn them back on.

### Device check and crash reports
- **Device check** on first start and after every update (and Settings → Run device check): Windows version, 64-bit,
  admin rights, data folder space, XInput, Visual C++ runtimes (one-click install from Microsoft), legacy DirectX
  runtime, .NET Framework 4.8, plus the optional tools features use (RTSS, RyzenAdj, ViGEmBus/HidHide), each with a
  fix link. The launcher is self-contained, so it never needs a separate .NET install.
- **Plain-English errors**: any error, at start-up or later, shows what happened, what to do and fix buttons (download the
  latest version, install the Visual C++ runtime, check Windows Security, free space, update graphics drivers), with the
  technical report, Copy report and Report the problem (a GitHub issue with your user name hidden). Crashes that close the
  launcher are reported at the next start, with the module Windows blamed.

### FPS cap per screen and cooling
- **FPS cap per PC and screen**: remember a different cap for the handheld screen, your monitor or TV, and for each PC
  (useful with library sync). Changing the target in-game saves it for the current screen.
- **Cooling modes** (per game, default in Settings): Balanced (CPU boost efficient), Cool (boost off, power −10%, earlier
  thermal guard that also trims power), Coolest (also power −20% and one FPS step lower). Everything is restored when the
  game closes, or at the next start after a crash. Settings → Cooling also lists practical ways to keep a device cool.

### Game updates
- **Official version check** once a day on the first launch (Settings → Updates), plus **Game updates** in the library
  toolbar and **Check** on each game page. It compares your installed version (from the repack folder's "v1.2.3", the exe,
  or the version you type) with official sources only: Steam patch notes and public build date for games matched to a
  Steam app id, and GOG's published builds. Games with a newer version get an **Update** badge.
- **Install update from file**: pick an update you already have (installer .exe, or a .zip/.7z/.rar of game files).
  Saves are backed up first; archives back up exactly the files they replace, installers get a full copy of the game
  (free space is checked first). The update is verified, and anything that fails is rolled back automatically.
- **History and restore**: each update keeps its backup; **Restore previous** puts the old version back without
  touching saves stored inside the game folder.
- **When it can't update safely** (unreadable file, unclear layout, not enough space, installer error), you get an
  alert and a step-by-step guide on the game page with links to the official patch notes, store page and support.
- The launcher doesn't search for or download games or updates from repack or download sites.

### Controllers
- **Menus with a controller** everywhere (library, game pages, Settings, Mods window, confirmation boxes): D-pad or left
  stick moves, **A** selects (on a game tile it plays), **B** goes back or closes, **LB/RB** switch pages or tabs, right
  stick scrolls, **Start** plays the selected game, **X** opens its mods, **Y** searches, **View** opens Big Picture.
  Text boxes open the touch keyboard. Turn off in Settings → Controllers.
- **Per-game controller settings:** stick deadzone, outer deadzone, anti-deadzone, response curve (linear, smooth,
  precise, aggressive), sensitivity and invert per stick; trigger deadzone and hair trigger; rumble strength (0–200%,
  with a test button); A/B X/Y swap. A live preview shows the stick as it is and as the game will get it. Set a default
  in Settings → Controllers and override it per game.
- These settings work through a virtual controller while the game runs, so they need two free drivers by Nefarius:
  **ViGEmBus** (virtual controller) and **HidHide** (hides the real controller from the game). The launcher detects them,
  hides only the devices it hid, and unhides them afterwards or at the next start after a crash. Xbox-style (XInput)
  controllers only, including handheld built-in controls.

### Look
The **Console** design (picked from mockups based on the awesome-design-md collection): top tabs, the selected game's art
filling the library behind everything, cover tiles that grow with a ring when focused, pill buttons and a big display
title. The **accent and background tint follow the selected game's cover art** (Settings → Look to turn off), with the
accent kept readable (4.5:1 for the Play button text).

### How it works page
A plain-language guide in the left pane (F1): what each feature does, a three-step quick start and shortcuts.

### Library, progress & Big Picture
- Status (Backlog / Playing / Beaten / 100% / Dropped), **tags**, favourites, hidden games, and sorting by recent,
  most played, A–Z, added or progress.
- **Hours totals** (all-time / this week) and a **progress bar** per game: hours played against the main-story length you
  enter, or a completion % you type in.
- **Big Picture mode (F11):** fullscreen and controller-first. Ⓐ Play, Ⓧ Optimise, LB/RB change the FPS
  target (1-click optimise), Ⓨ Favourite, Ⓑ Exit. Works with Xbox pads, and PlayStation pads via Steam Input or DS4Windows.
- **No UAC prompt** (optional desktop shortcut) and **start with Windows** (minimised), both via elevated Task Scheduler tasks.

### Display-aware launches (handheld ↔ monitor)
At every launch the active screen is detected (handheld panel or external monitor) and the game is set up for it:
- **Unreal:** your screen's native resolution, plus a **render scale** chosen to hit the FPS target on *that* screen
  (upscaled by the engine's TSR/FSR, so the image stays sharp). The first time on a new screen it's estimated from your
  FPS on the other screen by pixel count. After each session it's adjusted: down a step if you missed the target, back
  up once there's headroom. Each screen keeps its own setting per game.
- **Unity / Source:** the resolution is passed as launch options, so no files are changed.
- **Slower screens** (e.g. a 60 Hz TV) get a cap just under their refresh rate for that session.

### In-game overlay
A single clean RTSS line, `102 FPS · GPU 77°C · CPU 78°C`, with a short note while the thermal guard or a benchmark is
active. Choose Off / FPS + temps / Detailed in Settings.

### Updates
The launcher checks Releases at start-up and, by default, **installs a new version automatically** and restarts (never
while a game is running; turn it off in Settings → Updates to get a banner instead). Settings also has **Check for
updates** and **Reinstall latest version**, a force update that reinstalls the current release to repair damaged or
removed files. Updates swap the program files only: your library, settings and backups are kept. While the
repository is private, add a read-only GitHub token in Settings → Updates (it's stored encrypted).

### Hardware scan
On first run it scans CPU (hybrid P/E cores, X3D), GPUs and real VRAM, RAM speed/type/sticks, drive types
(NVMe/SSD/HDD), displays and **max refresh rates**, and Windows settings (Game Mode, HAGS, Memory Integrity,
Game Bar background recording, power plan, RTSS). It then gives machine-specific recommendations, e.g.
*"Display running at 60 Hz but supports 144 Hz"*, *"RAM at JEDEC 4800, enable EXPO"*,
*"single-channel RAM"*, *"3 games on a hard drive"*, *"GPU driver is 9 months old"*.

## Install & run

1. Download **`FitzLauncher-win-x64.zip`** from the latest [Release](../../releases/latest). Every successful build of
   `main` is published there automatically as `v0.3.N`.
2. Unzip anywhere (e.g. `C:\Games\FitzLauncher`) and run **`FitzLauncher.exe`**. It's self-contained, so no
   .NET install is needed. It asks for **administrator rights**: Game Mode, ETW FPS capture and sensors need
   them. Games are still started **un-elevated**, as if double-clicked, which keeps Steam/DRM happy.
3. Optional but recommended:
   - **RivaTuner Statistics Server** (free, guru3d.com) for the most even frame caps in every game.
   - **PawnIO** (pawnio.eu) for CPU temperatures. GPU temperatures work without it.

All data lives in `%LOCALAPPDATA%\FitzLauncher` (library, sessions, settings, artwork, config backups).

## Build from source

```powershell
dotnet test tests/FitzLauncher.Core.Tests
dotnet publish src/FitzLauncher.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o publish
```
Requires the .NET 10 SDK. The Core library and its tests also build and run on Linux/macOS.
Tag a commit `v0.1.0` (etc.) and CI publishes a GitHub Release with the zip.

## Project layout

| Project | What's in it |
|---|---|
| `src/FitzLauncher.Core` | Platform-neutral logic: models, storage, INI editing, game-file scanner, optimisation planner, hardware advisor, tweak engine (apply/revert), FPS statistics, artwork lookup |
| `src/FitzLauncher.Windows` | Windows services: WMI hardware scan, ETW FPS capture, LibreHardwareMonitor temps, Game Mode, power plans, RAM purge, un-elevated game launch, process tracking |
| `src/FitzLauncher.App` | WPF UI (MVVM, CommunityToolkit.Mvvm) |
| `tests/FitzLauncher.Core.Tests` | xUnit tests for the Core logic |

## Honest notes
- No tool can make a game render faster without changing *something*. The 1-click path changes only settings
  that don't alter the image: caps, GPU selection, presentation model, stutter fixes and streaming.
  Real FPS gains beyond that come from the graphics suggestions (your call) and the hardware recommendations
  (XMP/EXPO, refresh rate, dual-channel RAM, SSD), which are often the biggest wins.
- "RAM clearing" helps 1% lows mainly on 8-16 GB machines. On 32 GB+ the memory guard switches itself off.
- Unreal config tweaks need the game to have been launched once, so its config folder exists.
- Vulkan/OpenGL FPS is captured via kernel present events. If a game shows "no FPS data", tell me which one.

## Testing checklist
- [ ] Launcher starts, the hardware scan fills in the **Hardware & temps** page
- [ ] **Scan game folders** finds your games with the right exe; adding works; artwork appears
- [ ] Optimise at 117 on an Unreal game: `Engine.ini` gets `t.MaxFPS=117`; **Revert all** restores it
- [ ] Play: Game Mode messages appear, the launcher minimises, live FPS/temps show in the top bar
- [ ] After quitting: session summary toast with avg FPS / 1% low; history chart updates
- [ ] Services restart and the power plan returns to normal after the game closes

## Credits
- UI design pass guided by [unslop](https://github.com/heydamir/unslop) by Damir Sokolovsky ([@heydamir](https://x.com/heydamir)), CC BY 4.0.
- Frame-time capture follows the event model of Intel's [PresentMon](https://github.com/GameTechDev/PresentMon); sensors via [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor).
