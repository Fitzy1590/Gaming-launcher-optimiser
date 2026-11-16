---
paths: ["**/Emulators/**/*.cs", "**/Optimization/**/*.cs"]
---

# BlueStacks and LSFG rules (SPEC §15)

- Pure logic (config parsing, launch arguments, LSFG plan and GPU guard, settings patch) lives in `Core/Emulators` with a test. `Windows/Emulators` only finds, runs and writes.
- An Android game is a normal `Game` whose `ExePath` is `HD-Player.exe` and `InstallDir` is the BlueStacks folder, so the existing session (pinning, priority, Game Mode timer, Defender shield, RTSS cap) applies unchanged. No second path to any of those settings.
- Package and instance names are validated before they reach a command line (`BlueStacks.LaunchArguments`). Never build the argument text by hand.
- External tools (`HD-Adb.exe`) run with `UseShellExecute = false`, `CreateNoWindow = true` and a `WaitForExit` timeout.
- Lossless Scaling: key-level patch of its settings file, allow-listed keys, only existing elements, backup plus `lsfg-restore.json` first, restored at session end and at start-up. No hooks, no injection.
- Unverified until a device test: registry value names, `bluestacks.conf` keys, `--cmd launchApp`, Lossless Scaling `Settings.xml` path and key names. Say so in the UI text when a step finds nothing.
- Anti-cheat and online-only Android games are the user's call; the launcher never touches the game's own files.
