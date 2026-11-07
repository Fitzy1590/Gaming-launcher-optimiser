# Gaming-launcher-optimiser — Fitz Launcher

A fast, clean Windows game launcher for all your games and repacks, with one-click optimisation for
**stable 90 / 100 / 110 / 117 FPS**, an automatic **Game Mode**, a low-cost **FPS tracker** and
**temperature alerts**, all tailored to the PC it runs on.

> **Status: v0.2.** It compiles with zero warnings, and the core logic has 82 passing unit tests. It has
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

### Library, progress & Big Picture
- Status (Backlog / Playing / Beaten / 100% / Dropped), **tags**, favourites, hidden games, and sorting by recent,
  most played, A–Z, added or progress.
- **Hours totals** (all-time / this week) and a **progress bar** per game: hours played against the main-story length you
  enter, or a completion % you type in.
- **Big Picture mode (F11):** fullscreen and controller-first. Ⓐ Play, Ⓧ Optimise, LB/RB change the FPS
  target (1-click optimise), Ⓨ Favourite, Ⓑ Exit. Works with Xbox pads, and PlayStation pads via Steam Input or DS4Windows.
- **No UAC prompt** (optional desktop shortcut) and **start with Windows** (minimised), both via elevated Task Scheduler tasks.

### Hardware scan
On first run it scans CPU (hybrid P/E cores, X3D), GPUs and real VRAM, RAM speed/type/sticks, drive types
(NVMe/SSD/HDD), displays and **max refresh rates**, and Windows settings (Game Mode, HAGS, Memory Integrity,
Game Bar background recording, power plan, RTSS). It then gives machine-specific recommendations, e.g.
*"Display running at 60 Hz but supports 144 Hz"*, *"RAM at JEDEC 4800, enable EXPO"*,
*"single-channel RAM"*, *"3 games on a hard drive"*, *"GPU driver is 9 months old"*.

## Install & run

1. Download **`FitzLauncher-win-x64.zip`** from the latest [GitHub Actions run](../../actions) (Artifacts), or from a Release.
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
