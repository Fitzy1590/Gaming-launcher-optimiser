# Ideas & roadmap

## Built (v0.2), from your votes
| # | Feature | Notes |
|---|---------|-------|
| 1 | Auto-tune loop | One change per session, kept only if 1% lows improve ≥3% |
| 2 | CPU core pinning | V-Cache CCD (dual-CCD X3D) / P-cores (Intel hybrid); per-game override |
| 3 | Thermal guard | Live RTSS cap step-down and recovery |
| 4 | RTSS overlay line | Own OSD slot; doesn't touch Afterburner or RTSS global settings |
| 5 | Save-game backups | Before and after each session; emulator-aware save detection; restore |
| 6 | Repack runtimes | Detect, check and silently install bundled VC++/DirectX/.NET/PhysX/OpenAL/XNA |
| 7 | DLSS/XeSS/FSR 3.1 DLL updater | From your other games or a DLL folder; backed up, revertible |
| 8 | Reflex / Anti-Lag 2 | NVIDIA **and** AMD: in-game detection, driver fallback, RTSS limiter mode |
| 9 | No UAC prompt + start with Windows | Elevated scheduled tasks |
| 10 | Big Picture controller mode | XInput; LB/RB = 1-click FPS target |
| 14 | Benchmark mode | Ctrl+Alt+B, compare runs |
| 15 | Shader-cache tools | Sizes + clear; prompted after driver updates |
| 16 | Tags, status, sorting, hidden + hours totals and progress % | Progress = played ÷ main-story hours, or a manual % |
| 19 | Per-game presets | Framework + `presets.json`; send me your game list and I'll research verified fixes |
| 20 | Backups to Google Drive / USB / any folder | Offline USB catches up when reconnected |
| 21 | Use Afterburner's sensors | Avoids double polling |

## Not building (your call)
11 auto-import Steam/Epic, 13 pausing other launchers (you only use repacks), 17 HowLongToBeat dashboard, 18 Discord.

## Next ideas to vote on
| # | Idea | Why |
|---|------|-----|
| 22 | **Achievement progress for repacks**: read Goldberg/CODEX achievement files for a real completion % | Automatic progress without HowLongToBeat |
| 23 | **Per-game RTSS limiter mode**: let auto-tune compare async vs front-edge vs Reflex | Measured pacing instead of guessing |
| 24 | **Thermal guard fan-curve hint**: show when temps track GPU load (fan curve too lazy) vs ambient | Explains *why* it's hot |
| 25 | **Repack integrity check**: verify files against the repack's MD5/SFV after install | Catches corrupt installs early |
| 26 | **Session notes & screenshots folder per game** | Remember where you were |
| 27 | **Mod manager-lite**: enable/disable ReShade/ENB per launch | Quick FPS vs looks toggle |
