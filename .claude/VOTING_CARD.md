# Voting card: Game Booster and diagnostics upgrade

Audit found four of six pillars already built (suspend + services, P-core/X3D pinning + High priority, 0.5 ms timer, standby purge, core parking). Option A therefore covers only the gaps.

| | **A (recommended): delta only** | **B: basic tuner** |
|---|---|---|
| Ultimate Performance plan check, restore at exit | yes | yes |
| Equalizer APO profile (`Include:` line, backed up, restored) | yes | no |
| Diagnostic logger, default OFF, `[REDACTED]` filter | yes | no |
| Export Diagnostic Package (one `.txt`) | yes | no |
| Booster page (instant swap, existing theme tokens) | yes | no |
| Governance: steward agent, booster/win32 rules, deny list, concise output | yes | no |
| Duplicates existing suspender, timer, affinity, purger | no | no |

HUD palette (mapped to existing tokens, no retheme): Obsidian `#0B0E14` to `Bg`, Cyber Teal `#00F2FE` to `Accent`, Emerald `#10B981` to `Good`, Amber `#F59E0B` to `Warn`. Fonts: Inter UI, JetBrains Mono metrics.

- [ ] **1 Option A** Delta only (recommended)
- [ ] **2 Option B** Priority + power plan only

Details: `SPEC.md` §17. Interactive version: `VOTING_CARD.html`.
