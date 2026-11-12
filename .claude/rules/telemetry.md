---
paths: ["**/Telemetry/**/*.cs", "**/Optimization/**/*.cs"]
---

# Telemetry and tuning rules (SPEC §12)

Decision logic is pure in `Core/Telemetry` with a test. Windows code gathers inputs and applies results. Reuse `CpuAffinityPlanner`, `ResourceApplier`, `GameModeController`, `MemoryCleaner`, `PacingTuner`, `ThermalGovernor`, `AutoTdpGovernor`; do not write a second path to the same setting.

## The four numeric rules (one place: constants in `ClosedLoopTuner`, no magic numbers elsewhere)
1. **CPU-bound:** CPU busy / GPU busy > 1.15 for 3 s → high priority, P-core affinity, hold back background apps (priority, not service suspension). Cooldown 60 s; release below 1.05 for 15 s.
2. **GPU-bound:** GPU busy / CPU busy > 1.15 for 3 s → raise the power profile if the machine has one; check HAGS and advise only. Cooldown 60 s. Advice only while Auto TDP is On.
3. **Micro-stutter:** 1% low < 70 % of average for 3 s → frame cap at monitor Hz − 3, trim working sets. Cooldown 30 s; release at ≥ 80 % for 30 s.
4. **Thermal:** CPU or GPU > 85 °C → stock power profile and a non-activating toast. Cooldown 20 s; release below 80 °C for 30 s. Defers to `ThermalGovernor`. Beats every other rule.

## Contracts
- A rule fires only after 3 consecutive 1 s buckets; max 3 applications per rule per session; a change that made things worse over 16 s is undone and not retried.
- 1% low = 1000 / p99 frame time over a time-based window (60 s). Frames over 1 s count as stalls. Never use a frame-count window.
- No busy metrics (not elevated, events missing) → Rules 1 and 2 do nothing and say so. Never guess.
- Restore record (`closed-loop-restore.json`) is written before any change; `RecoverIfNeeded` at start-up; everything is put back at session end.
- ETW only. No hooks, no injection, no reading game memory. Skip priority, affinity and memory actions for anti-cheat games.
- Background threads catch specific exception types with `when` filters and never throw out. `Update` is called under a lock from one sampler thread; applying runs on `Task.Run` with a `CancellationToken` and `ConfigureAwait(false)`.
- Event IDs and provider GUIDs in SPEC §12.1 are unverified. Mark anything you rely on as verified only after checking it against PresentMon's source or a live trace.
