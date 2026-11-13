---
paths: ["**/Graphics/**/*.cs", "**/Optimization/**/*.cs", "**/Upscalers/**/*.cs"]
---

# Upscaler engine rules (SPEC §13)

Plan logic is pure in `Core/Upscalers` with a test; Windows code only copies files. Reuse `FileReplaceAction`, `AppliedChange`, `UpscalerAdvisor`; no second apply path.

- Skip anti-cheat games (`GameModLayout.HasAntiCheat`). No hooks, no injection, no reading game memory: we only place files.
- Write `upscaler-restore.json` before any copy. Back up every file we replace as `<name>.bak` (keep the original attributes), then copy temp-then-replace. `RecoverIfNeeded` at start-up.
- Disable restores from `.bak`, verifies SHA-256, and leaves a file the user changed since alone and says so.
- Only copy to the game's own install folder. Never overwrite without a `.bak`. Check the target is inside the install dir (no `..`).
- `OptiScaler.ini`: key-level generation from the template; unknown keys are kept. Ratios and upscaler choice come from the plan, not magic numbers.
- Download OptiScaler only from its official GitHub Releases (host allow-list, SHA-256 from the asset `digest`, zip-slip guard). Never bundle it (GPL-3.0).
- Catch `IOException`, `UnauthorizedAccessException`, `Win32Exception` with `when` filters; never throw out of a background thread; no UI-thread blocking.
- FSR 4 only on RDNA3/4; say "FSR 4.x". Proxy DLL names and uninstall steps are unverified until U2 marks them verified.
