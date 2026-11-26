---
paths: ["**/Audio/**/*.cs", "**/Diagnostics/**/*.cs", "**/GameModeController.cs", "**/Telemetry/FramePacing.cs"]
---

# Booster engine rules (SPEC §17)

Six of the seven pillars were already built; reuse them (`GameModeController`, `CpuAffinityPlanner`, `MemoryCleaner`, `PowerPlans`, `EtwFpsTracker`, `UpscalerLibrary`). No second path to the same setting.

1. **Equalizer APO:** edit only Fitz's own `fitz-profile.txt` and one `Include:` line in `config.txt`. Backup first, record in `eqapo-restore.json`, remove only Fitz's lines at exit and at start-up. Not installed means off with a hint, never an error.
2. **Diagnostics:** `Log.Trace` does nothing unless the setting is on. Every log write and the export go through `LogRedactor` (keys, tokens, cookies, `SWID`, `ESPN_S2`, user paths). New secret patterns get a test first.
3. **Timers:** `NtSetTimerResolution(5000)` (game session) and `timeBeginPeriod(1)` (controller poll loop, `Controllers.cs`) are different jobs; Windows keeps the finest request, so they do not conflict. Do not add a third requester.
4. **Frame pacing:** ETW only (`EtwFpsTracker`). No PresentMon binary, no injection.
5. **DLL floors:** DLSS 3.7 and XeSS 1.3 only. FSR is unchecked until its file-version scheme is verified.
