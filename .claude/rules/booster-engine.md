---
paths: ["**/GameModeController.cs", "**/PowerPlans.cs", "**/Diagnostics/**/*.cs", "**/Views/BoosterView.xaml", "**/ViewModels/BoosterViewModel.cs"]
---

# Booster and diagnostics rules (SPEC §17)

One path per setting. Suspend/resume, service stop/restore, timer, standby purge, core parking, priority and pinning stay in `GameModeController`, `ClosedLoopApplier`, `CpuAffinityPlanner`, `MemoryCleaner`. Do not add a second suspender, timer request, affinity writer or purger.

1. **Power plan:** `PowerPlans.EnsureUltimate()` picks Ultimate Performance, creates it from the template, or falls back to High performance. The previous plan is recorded first and restored at exit and at start-up. Never Realtime priority.
2. **Diagnostic trace:** `DiagnosticLog` is off by default and does nothing while off. Everything stored or exported passes through `DiagnosticLog.Sanitize`. The report is written only on the button press, never sent anywhere.
3. **Booster page:** read-only status plus export. Instant `Visibility` swap, no Frame, Storyboard or transitions. Colours from `Theme.xaml` tokens only; no wholesale retheme.
4. **Audio:** no equaliser or sound-driver file edits (FxSound/Equalizer APO formats unverified).
5. **Plain English:** every message says what changed and how to undo it.
