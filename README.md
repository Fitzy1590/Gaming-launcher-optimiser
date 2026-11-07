# Gaming-launcher-optimiser — Fitz Launcher

A fast, clean Windows game launcher for all your games and repacks, with one-click optimisation for
**stable 90 / 100 / 110 / 117 FPS**, an automatic **Game Mode**, a low-cost **FPS tracker** and
**temperature alerts**, all tailored to the PC it runs on.

> **Status: v0.1, first build.** It compiles, and the core logic has 51 passing unit tests. It has
> **not been run on a real Windows PC yet** (it was built in a Linux container). Expect a first round
> of fixes once you try it. See [Testing checklist](#testing-checklist). Ideas to vote on: [docs/IDEAS.md](docs/IDEAS.md).

## Features

### Library
- **Add games manually:** pick an `.exe` or shortcut, use **Add folder of games** (each subfolder becomes a game),
  or **drag & drop** exes, shortcuts or folders onto the window.
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
- [ ] Adding an exe / folder / drag-drop works; artwork appears
- [ ] Optimise at 117 on an Unreal game: `Engine.ini` gets `t.MaxFPS=117`; **Revert all** restores it
- [ ] Play: Game Mode messages appear, the launcher minimises, live FPS/temps show in the top bar
- [ ] After quitting: session summary toast with avg FPS / 1% low; history chart updates
- [ ] Services restart and the power plan returns to normal after the game closes
