---
name: add-emulator
description: Add a new emulator to the Emulation Hub, or verify/correct an existing one (download source, asset pattern, launch arguments, data folders, tuning keys, mod folders). Use when asked to support an emulator, fix its updater, or mark a source as verified.
---

# Add or verify an emulator

All tables are pure Core code with tests; Windows/App code only applies them. Read `SPEC.md` for the stage the work belongs to.

## Touch points, in order

1. **Platform** — `Core/Emulation/Platforms.cs`: add an `EmuPlatform` only if the console is new (`PlatformInfo`: name, ROM extensions, IGDB id, emulator ids).
2. **Source** — `Core/Emulation/EmulatorSources.cs` → `Defaults`: `Id` (lower-case, stable: it is stored in the library), `Name`, `Platforms`, `ExeNames`, `Type` (`GitHub`, `Website`, `Manual`), `Repo`, `AssetPattern`, `PortableMarker`, `DataDirs`, `ArgsTemplate` (`{rom}`), `SupersededBy`/`SupersededReason`. New entries start `Verified = false`.
3. **Discovery** — `Core/Emulation/EmulatorDiscovery.cs`: default install paths and how the version is read.
4. **Releases** — `Core/Emulation/EmuReleases.cs`: the asset picker scores names; add the project's naming only if the scorer picks wrongly.
5. **Tuning** — `Core/Emulation/EmuTuning.cs` → `EmuTuneMaps.Maps`: settings file, format (ini/toml/yaml/json) and the keys for the 8 quality levels. `ConfigPatcher` only edits keys that already exist. No map = no auto-tune, which is acceptable.
6. **Mods** — `Core/Emulation/EmuMods.cs`: destination folder per `EmuModKind`.
7. **Header/ID reader** — `Core/Emulation/RomHeaders.cs`, only for a new console.

## Verifying a source (what flips `Verified` to true)

Check against the emulator's own GitHub repository, nothing else:

- The repo exists and is the official one (linked from the project's own site/README). Forks only when `SPEC.md` §9 names them.
- Fetch `https://api.github.com/repos/<owner>/<name>/releases/latest` (and one nightly/pre-release if the project has them). The `AssetPattern` must match exactly one Windows x64 asset in each. Paste real asset names into the test.
- `ArgsTemplate` flags come from the emulator's own `--help` or documentation. Cite the page in a comment.
- `DataDirs` and `PortableMarker` come from the emulator's documentation on portable mode. A wrong `DataDirs` entry risks saves on update: if unsure, leave `Verified = false` and say what is unchecked.
- Tuning keys are per emulator **version**. Unknown version = preview only, never write.

If the network policy blocks `api.github.com` for that repo, say so and leave `Verified = false`. Do not guess from memory.

## Tests (synthetic data only)

- `EmulationModelTests`: defaults load, ids unique, an edited `emulator-sources.json` overrides by id.
- `EmulatorDiscoveryTests`: found in a temp folder tree, version read, portable marker.
- `EmuReleaseTests`: the real asset names from the release JSON pick the right file; version compare orders them; unsafe archive paths are rejected.
- `ConfigPatcherTests`, `EmuTunerTests`: edits keep comments and key order; only existing keys change.
- `EmuModStoreTests`: mod destination, conflicts never overwritten, toggle off removes exactly its files.

Run `dotnet test tests/FitzLauncher.Core.Tests --filter "EmulationModelTests|EmulatorDiscoveryTests|EmuReleaseTests|ConfigPatcherTests|EmuTunerTests|EmuModStoreTests"`.

## Hard limits

- Never download, open or bundle ROMs, BIOS, firmware or keys (`prod.keys`, `title.keys`). Tests use byte arrays built in code.
- Downloads only from the emulator's own GitHub Releases (host allow-list in `App/Services/EmulatorInstaller.cs`), SHA-256 checked when GitHub publishes a digest, unpacked with the zip-slip guard.
- Config edits: key-level, allow-listed, temp-then-replace, backup and `AppliedChange` record first.
- No hooks or injection into the emulator process.
- Finish by updating the matching stage line in `FEATURE_PROGRESS.md` §4.
