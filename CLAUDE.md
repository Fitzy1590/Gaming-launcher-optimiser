# Fitz Launcher

WPF game launcher and optimiser for Windows handhelds and PCs (.NET 10). Progress across sessions: `FEATURE_PROGRESS.md`.

## Build & test

```bash
dotnet build FitzLauncher.slnx                 # all projects; Windows targets compile on Linux (EnableWindowsTargeting)
dotnet test tests/FitzLauncher.Core.Tests      # xUnit, Core only (~200 tests, ~2 s)
dotnet test tests/FitzLauncher.Core.Tests --filter <ClassName>
dotnet run --project src/FitzLauncher.App      # Windows only; WPF does not run on Linux
dotnet format whitespace FitzLauncher.slnx --include <file.cs>   # format one file (the hook does this)
```

- The solution file is `FitzLauncher.slnx` (not `.sln`).
- CI (`.github/workflows/build.yml`, windows-latest): test, then publish win-x64. Every push to `main` becomes Release `v0.3.<run>`, which the in-app updater installs.

## Layout

| Project | Target | Holds |
|---|---|---|
| `src/FitzLauncher.Core` | `net10.0` | Models, settings, planners, parsers, pure logic. No Windows APIs, so it's testable. |
| `src/FitzLauncher.Windows` | `net10.0-windows` | OS integration: P/Invoke, RTSS, RyzenAdj, ETW FPS, sensors, controllers (XInput, Sony HID, ViGEm, HidHide), power and sleep. |
| `src/FitzLauncher.App` | WPF | Views (XAML), ViewModels (CommunityToolkit.Mvvm), app services, `Theme.xaml` design tokens. |
| `tests/FitzLauncher.Core.Tests` | `net10.0` | Tests for Core only. |

## Conventions

- **Naming:** PascalCase for types and members, `_camelCase` for private fields, `Async` suffix on awaitable methods. File-scoped namespaces. Nullable is on, so no `!` without a reason.
- **Where logic goes:** decision logic (caps, ladders, parsers, formatters, advisors) lives in Core with a test. Windows/App code only gathers inputs and applies results.
- **MVVM:**
  - Use `[ObservableProperty]`/`[RelayCommand]`; no logic in code-behind beyond view plumbing.
  - Settings bind through VM properties that save. Debounce saves fed by sliders.
- **async/await:**
  - Never block the UI thread: wrap process/registry/disk/network work in `Task.Run`.
  - Pass `CancellationToken` through and use `ConfigureAwait(false)` in Windows/Core.
  - Only event handlers may be `async void`. Marshal back with `Dispatcher.BeginInvoke`.
- **Process isolation:**
  - Run external tools (powercfg, RyzenAdj, HidHideCLI) via `ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true }` with a `WaitForExit` timeout.
  - Never inject into game processes.
  - Skip anti-cheat games for DLL swaps.
- **Revertible by default:**
  - Every change to a game, the OS or the hardware writes a restore record first (`*-restore.json`, `AppliedChange`) and has a `RecoverIfNeeded` at start-up.
  - Power limits, overlays and caps are put back when the session ends.
- **Exceptions:** catch specific types (`IOException`, `Win32Exception`, …) with `when` filters. Background threads never throw out.
- **User-facing text:** plain English, UK spelling, no jargon without an explanation.

## Boundaries

- Never edit generated files: `obj/`, `bin/`, `*.g.cs`, `*.g.i.cs`, CommunityToolkit source-generator output.
- **Native interop** (`Native.cs` and `[DllImport]` declarations in `FitzLauncher.Windows`):
  - Don't change signatures, struct layouts or constants unless the task requires it.
  - Check against the Win32 docs and note the source in a comment.
  - Add new P/Invoke next to the feature that uses it.
- Never commit secrets: API keys are DPAPI-encrypted in settings. Never read `.env`, `appsettings.Development.json` or credential files.
- Updates and downloads come only from official sources (publisher, GitHub releases). Never search or download from repack or warez sites.
- Git:
  - Commit as `Fitzy1590 <fitzy151290@hotmail.com>`.
  - Push to `main` (it publishes a release). No PRs unless asked.
- Don't restyle the app wholesale. Visual rules are in `.claude/rules/windows-ui.md`.
