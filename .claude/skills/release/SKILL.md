---
name: release
description: Ship a Fitz Launcher release (push to main, watch CI, confirm the GitHub Release the in-app updater installs) or diagnose a release that did not appear. Use when asked to release, ship, publish, or check why an update is not showing.
---

# Release runbook

Every push to `main` is a release: `.github/workflows/build.yml` (windows-latest) tests, publishes win-x64 self-contained, zips it and creates Release `v0.3.<run_number>` marked latest. The in-app updater compares its own version with that tag. There is no manual version bump.

## Before pushing

1. `dotnet build FitzLauncher.slnx` → 0 errors (WPF compiles on Linux, it does not run).
2. `dotnet test tests/FitzLauncher.Core.Tests` → 0 failed. CI runs the same tests in Release; a failure there means no release.
3. `git status` is clean apart from the intended change. No `obj/`, `bin/`, keys or settings files staged.
4. Commit as `Fitzy1590 <fitzy151290@hotmail.com>`. The first line of the commit is what users read in the generated release notes: plain English, UK spelling, say what changed for the player.
5. Tick or add the item in `FEATURE_PROGRESS.md` in the same commit. Use "Not yet confirmed on device" until it has been tried on the handheld.

## Push and confirm

1. `git push origin main` (only when the session is allowed to push to `main`; otherwise push the session branch and say that no release was made).
2. Find the run: GitHub MCP `actions_list` for workflow `build.yml`, newest first. Wait for it to finish; do not poll in a tight loop.
3. Green: `get_latest_release` → tag `v0.3.<run>` with asset `FitzLauncher-win-x64.zip`. Report the tag.
4. Red: `get_job_logs` for the failed job. Usual causes:
   - **Test step**: a test that passes on Linux but not Windows (path separators, line endings, culture). Reproduce with `--filter <ClassName>`, fix, push again.
   - **Publish step**: XAML or Windows-only compile error that `EnableWindowsTargeting` did not surface. Read the first `error` line, not the last.
   - **Publish release step**: tag already exists (a re-run of an old run). Push a new commit instead of re-running.

## Rules

- Never skip or disable a test to get a release out.
- Never push an empty commit to kick CI.
- Never create a tag or Release by hand: the updater expects the CI-built zip layout.
- A pull request build never publishes (the step is guarded on `refs/heads/main`).
