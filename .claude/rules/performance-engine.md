---
paths: ["**/Optimization/**/*.cs", "**/Services/**/*.cs", "**/Shaders/**/*.cs", "**/GameModeController.cs"]
---

# Performance suite rules (SPEC §14)

Decision logic is pure in Core with a test. Windows code gathers inputs and applies results. Reuse `GameModeController`, `SessionHolds`, `ShaderCacheTool`, `OptiScalerTweak`/`TweakEngine`, `CpuAffinityPlanner`, `ClosedLoopTuner`; no second path to the same setting.

1. **Timer:** `NtSetTimerResolution(5000)` (0.5 ms) is asked for at launch and released at exit through `GameModeRestoreState`. Windows 11 is opt-in (`TimerOnWindows11`), because there the benefit goes only to the app that asks.
2. **Shader caches:** never cleared while a game session is running. Files in use are skipped, never forced.
3. **Defender and maintenance:** scans are made idle-only, never switched off; real-time protection is not touched. Restore record first, undo at exit and at start-up.
4. **OptiScaler:** never bundled (GPL-3.0). Anti-cheat games refused. A file whose size no longer matches what Fitz placed is reported, not overwritten.
5. **CPU-bound rule:** busy readings are an estimate from GPU load (`BusyEstimator`), "can't tell" at the cap or refresh rate. Do not claim ETW or PresentMon numbers until the event IDs are verified.
6. **Plain English:** every message the user sees says what changed and how to undo it.
