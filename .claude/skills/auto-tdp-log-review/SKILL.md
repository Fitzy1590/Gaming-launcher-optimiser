---
name: auto-tdp-log-review
description: Analyse an Auto TDP session CSV from a handheld and turn what it shows into a governor change with a test. Use when the user shares or pastes an auto-tdp CSV, or reports that the power limit dived, stuck, oscillated or ran hot in a session.
---

# Auto TDP log review

The governor is pure logic in `src/FitzLauncher.Core/Optimization/AutoTdp.cs` (`AutoTdpGovernor`), fed and logged by `src/FitzLauncher.Windows/GameSession.cs` (`OpenAutoCsv`/`WriteAutoCsv`). Tests: `tests/FitzLauncher.Core.Tests/AutoTdpTests.cs`.

## The file

`<data root>\auto-tdp\<Game-Name>-yyyyMMdd-HHmm.csv`, kept 14 days. Line 1 is a comment: game, target FPS, `observe` (Watch only) or `on`, watt range. Then one row per governor tick:

| Column | Meaning |
|---|---|
| `seconds` | Since session start |
| `limit_w` | Limit in force during this tick |
| `frames`, `fps`, `low1_fps` | Frames counted, average, 1% low (already clamped to the cap) |
| `drawn_w` | Package power reading. Can under-report (seen 25 W of a 29 W limit) |
| `temp_c` | Hottest of CPU/GPU used for decisions |
| `thermal_hold` | 1 while the thermal guard holds |
| `new_limit_w`, `note` | The decision this tick and its plain-English reason (empty = no change) |
| `target_fps`, `cap_fps` | Target and the FPS cap being held |
| `cpu_c`, `gpu_c`, `cpu_w` | Separate sensors |

## Method

1. Parse with a script (python `csv`, skip the `#` line). Do not eyeball long files.
2. Summarise first: duration, min/median/max `limit_w`, share of ticks with `low1_fps` ≥ 90 % of target, count of decisions grouped by `note`, temperature range.
3. Find the episodes the user felt. Look for:
   - **Dive**: `limit_w` falling while `fps` is under target → which `note` drove it.
   - **Plateau**: FPS under target for > 30 s with no raise → what blocked it (hold, ceiling, heat, "limit not binding").
   - **Oscillation**: up/down pairs within 60 s.
   - **Heat**: `temp_c` over the guard trigger → how long, and what ended it (in past logs, only cooling ended a plateau).
   - **Menus**: very high FPS, low `drawn_w` → the limit must not chase it to the floor.
4. Quote the rows (seconds, limit, fps, temp, note) that show the problem before proposing anything.
5. Name the one rule responsible in `AutoTdp.cs`. Change the smallest thing: a threshold, a spacing, a condition. Keep constants named.
6. Write the test first in `AutoTdpTests.cs`, replaying the logged sequence as samples (synthetic numbers taken from the rows). It must fail before the change and pass after. Run `--filter AutoTdpTests|ThermalGuardFutilityTests`.
7. Check the change does not undo an earlier fix: read the long Auto TDP entry in `FEATURE_PROGRESS.md` §2 and make sure each behaviour listed there still has a passing test.
8. Extend that entry with one sentence on the new behaviour and the evidence ("from a real session log"). Keep "Not yet confirmed on device".

## Rules

- One sensor reading is noise: the CPU sensor swings 10-20 °C in seconds. Judge heat over a window.
- FPS above the cap is not headroom. A low 1% low with a healthy average is a hitch, not a need for watts.
- Never widen the 25-60 W range or remove the restore-at-exit path to fix a symptom.
- If the CSV lacks the data to decide, add a column in `WriteAutoCsv` (append at the end so old files still parse) and ask for another session in Watch only.
