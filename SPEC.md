# Emulation Hub: technical specification

Status: approved scope (all voted items), nothing built yet. Values marked **VERIFY** were written from memory and must be checked against the live project before code relies on them. Each has a test or a "Test sources" button in the stage that uses it (`FEATURE_PROGRESS.md` §4).

## 0. Scope and rules

- Platforms (first release): Switch, 3DS, PS2, PS3, PSP, GameCube/Wii, N64, Xbox 360.
- Emulators: PCSX2, RPCS3, PPSSPP, Dolphin, Lime3DS and Azahar (3DS), a Switch emulator (Ryujinx forks; **VERIFY** the current maintained project), Xenia Canary, an N64 emulator (RMG or Project64, user's choice).
- Never download or read ROMs, BIOS, firmware, keys or game packages. Never inject into emulator processes. Third-party catalogues of game versions are out unless the user approves (see §7 open decisions).
- Folder mapping: requested `Services/Emulators|Scrapers|Overlay` become `Core/Emulation`, `Windows/Emulation`, `App/*/Emulation` (existing project layout).

## 1. Emulator discovery and updater

### 1.1 Source table (`emulator-sources.json`, shipped with the app, editable)
```json
{ "id": "pcsx2", "platforms": ["ps2"], "exe": "pcsx2-qt.exe",
  "source": { "type": "github", "repo": "PCSX2/pcsx2", "channel": "stable|nightly",
              "assetPattern": "windows.*x64.*\\.(7z|zip)$", "versionFrom": "tag_name" },
  "portableMarker": "portable.ini", "dataDirs": ["inis","memcards","sstates","cache"] }
```
All `repo`, `assetPattern`, `exe`, `portableMarker`, `dataDirs` values are **VERIFY**. Expected: PCSX2 `PCSX2/pcsx2` (`pcsx2-qt.exe`, `portable.ini`); RPCS3 `RPCS3/rpcs3-binaries-win` (`rpcs3.exe`, config beside exe); Dolphin has no GitHub release assets, so source type `website` (**VERIFY**, `portable.txt`); Lime3DS `Lime3DS/lime3ds-archive` and Azahar `azahar-emu/azahar` (`user` folder); Xenia Canary `xenia-canary/xenia-canary-releases` (`portable.txt`); PPSSPP `hrydgard/ppsspp` (**VERIFY** whether Windows builds are release assets; `memstick`); Switch and N64 sources chosen at build time.

### 1.2 GitHub Releases API
- `GET https://api.github.com/repos/{owner}/{repo}/releases/latest` (stable) and `.../releases?per_page=10` (nightly: first non-draft, optionally prerelease).
- Fields used: `tag_name`, `name`, `prerelease`, `draft`, `published_at`, `assets[].name`, `assets[].size`, `assets[].browser_download_url`, `assets[].digest` (`sha256:...`, only when present).
- Unauthenticated limit is 60 requests an hour: send `If-None-Match` with the cached ETag (a 304 does not count as a full call), check at most once every 6 hours per emulator, optional token stored DPAPI-encrypted.
- Version extraction: first match of `v?(\d+(?:\.\d+){1,3})` in `tag_name`, else in `name`; nightly builds use the trailing build number `-(\d+)` or the `published_at` timestamp. Comparison is a pure function in Core with tests (semantic, then build number, then date).

### 1.3 Install layout (updates never touch user data)
```
<EmuRoot>\<id>\versions\<version>\   program files
<EmuRoot>\<id>\data\                 settings, saves, memory cards, caches (shared)
<EmuRoot>\<id>\current.json          {"version":"..."}  (pointer, not a symlink)
```
Each version folder gets a directory junction (no admin needed) for each entry in `dataDirs` into `data\`, and the portable marker file, so every version sees the same data. Rollback = rewrite `current.json`. Keep the last 2 versions.

### 1.4 Download and extract
- Hosts allowed: `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com` (**VERIFY**). Anything else is refused.
- Stream to `%TEMP%` with resume, verify size, then SHA-256 against `digest` when present. No digest = show "not verified" in the UI.
- `.zip` via `System.IO.Compression`; `.7z` via a small library (SharpCompress, check licence at build time). Extraction guards against zip-slip (reject entries that resolve outside the target) and caps total size.
- Process: write the restore record, extract to `versions\<v>.partial`, rename, update `current.json`. A crash leaves the old version active (`RecoverIfNeeded` clears partials).

### 1.5 Discovery
Scan, in order: `<EmuRoot>`, the user's chosen folders, `%ProgramFiles%`, `%LocalAppData%\Programs`, `%AppData%`, and each drive's `Emulators`/`Games` folders (depth 3). Match by exe name from the source table, read the file version for the installed version, mark portable if the marker exists. Never scans system folders recursively.

## 2. Metadata and artwork

### 2.1 Matching (pure, in Core)
Order: serial / title ID, then cleaned file name (reuse `GameNameCleaner`), then manual re-match. Header parsers take a `ReadOnlySpan<byte>` so tests use synthetic bytes:
- PS2: `SYSTEM.CNF` `BOOT2 = cdrom0:\SLUS_123.45`. PS3 and PSP: `PARAM.SFO` `TITLE_ID` / `DISC_ID`.
- GameCube/Wii: 6-char game ID at offset 0. N64: header game code. 3DS: product code in the NCCH header.
- Xbox 360: title ID from the XEX2 execution-info header or an STFS package header.
- Switch: title ID from the file name tag `[0100...]` only. Reading NSP/XCI contents needs keys, which we never read.

### 2.2 Providers (one interface `IMetadataProvider`, results merged by priority)
- **SteamGridDB** (artwork): base `https://www.steamgriddb.com/api/v2`, header `Authorization: Bearer <key>`. `GET /search/autocomplete/{term}`, then `/grids/game/{id}`, `/heroes/game/{id}`, `/logos/game/{id}`, `/icons/game/{id}` (filters `dimensions`, `types`, `nsfw=false`, `humor=false`).
- **IGDB** (text): Twitch OAuth `POST https://id.twitch.tv/oauth2/token` (`client_credentials`), then `POST https://api.igdb.com/v4/games` with headers `Client-ID` and `Authorization: Bearer`, body e.g. `search "name"; fields name,summary,first_release_date,genres.name,total_rating,involved_companies.company.name,cover.image_id; where platforms = (8);` Platform IDs (**VERIFY**): PS2 8, PS3 9, PSP 38, Switch 130, 3DS 37, GameCube 21, Wii 5, N64 4, Xbox 360 12. Limit 4 requests a second.
- **ScreenScraper** (retro and regional art, fallback): `jeuInfos.php` by `crc`/`romnom` + `systemeid`; needs the developer ID/password the site issues plus the user's account (**VERIFY** current rules); thread count limited per account, so use 1 request at a time.
- Keys live in `LauncherSettings` DPAPI-encrypted. A missing key disables that provider and shows "Add key in Settings → Emulation". No provider ever throws into the UI.

### 2.3 Cache
`%AppData%\FitzLauncher\emu-art\{provider}\{gameKey}\{kind}-{hash}.{ext}`; originals kept up to 4K, a 400 px thumbnail generated for the grid; TTL 30 days (7 days for "not found"); a per-provider `SemaphoreSlim` plus token bucket; conditional requests where supported; all calls `async` with `CancellationToken`.

### 2.4 Collections
Stored in `Library.json`: platform (automatic), genre (automatic), recently played, favourites, user collections.

## 3. Mods, patches and graphics packs

### 3.1 Sources
- **GitHub repos** (curated list in `emulator-sources.json`, `mods` array): releases or a tree download, no key.
- **GameBanana**: `https://gamebanana.com/apiv11/...` (**VERIFY** endpoints: game lookup, mod index by game, mod profile with `_aFiles[]` download URLs). No key; descriptive User-Agent; cache responses 1 hour.
- **Nexus** (only games it hosts): the existing `NexusClient` and key.
- Archives are downloaded from the listed file URLs only, extracted with the zip-slip guard, size cap 4 GB, no executables run.

### 3.2 Destination map (**VERIFY** each against the installed version)
| Emulator | Mods / patches | Textures |
|---|---|---|
| Switch (Yuzu-style) | `load\<titleid>\<mod>\{romfs,exefs}` | same |
| Switch (Ryujinx-style) | `mods\contents\<titleid>\<mod>\` | same |
| 3DS (Lime3DS / Azahar) | `load\mods\<titleid>\` | `load\textures\<titleid>\` |
| PS2 (PCSX2) | `patches\<CRC>.pnach` (cheats in `cheats\`) | `textures\<serial>\replacements\` |
| PS3 (RPCS3) | `patches\` YAML patch files | n/a |
| GC/Wii (Dolphin) | `Load\GraphicMods\<name>\`, `Load\Riivolution\` | `Load\Textures\<gameid>\` |
| Xbox 360 (Xenia) | `patches\<titleid>.patch.toml` | n/a |
| PSP (PPSSPP) | cheats `PSP\Cheats\` | `PSP\TEXTURES\<DISC_ID>\` |

### 3.3 Toggles and restore
Downloaded mods are stored once in `<EmuRoot>\mods-library\<id>\<modId>\`. Enabling copies (or hard-links when on the same drive) into the destination and writes a record of every file and its hash; disabling removes exactly those files; a changed file is reported, not deleted. Conflicts (two mods writing the same path) are listed before enabling.

## 4. Update and DLC detection

- Local pass: for each game, read title ID and version from the dump (§2.1) and, for Switch, relate base / update / DLC files already in the user's library by title ID (update ID = base ID + `0x800`, DLC IDs sit in the range above `base + 0x1000`; **VERIFY**).
- Online latest version: from the emulator's own update data only where it exists (RPCS3 reads the PS3 update list itself). Nothing is downloaded: the banner says "Newer version available" and links out.
- Records in `emu-versions.json`: `{ gameKey, titleId, localVersion, latestKnown, checkedAt, source }`. Check at most once a day.
- **Open decision**: a Switch/PS2 online version source needs a third-party catalogue or an official endpoint. Default until you decide: local detection only, plus emulator-native sources.

## 5. Auto-tuner

### 5.1 Hardware profile
Reuse the device check: GPU vendor, name, VRAM, CPU cores, APU handheld flag, current screen resolution and refresh rate. Output record `HardwareProfile`.

### 5.2 Config edits
`ConfigEdit { File, Format(ini|toml|yaml|json), Section, Key, Value, MinVersion?, MaxVersion? }`.
- Applied as key-level patches: INI/TOML/YAML change the matching line only (comments and order kept), JSON uses a DOM write. A key not in the per-emulator allow-list is never added.
- Flow: backup the file to `emu-restore\<id>\<timestamp>\`, write `*-restore.json` (`AppliedChange`), write temp, atomic replace. `RecoverIfNeeded` at start-up. "Revert tuning" restores the backup unless the user changed the file since (then it asks).
- Emulator closed check before writing (emulators rewrite config on exit).

### 5.3 Presets (pure planner `TuningPlanner.Plan(hw, emulator, preset, game?)`)
| Preset | Backend | Resolution | Shaders | CPU accuracy |
|---|---|---|---|---|
| Power Saver | most stable API | 1x, FPS cap | cache on, async | fastest accurate-enough |
| Balanced | Vulkan (D3D12 on NVIDIA/Xbox 360) | 1x to 2x by VRAM | async compile | default |
| Ultra | Vulkan | 3x to 4K by VRAM, only on a dedicated GPU | pre-compile, async | accurate, more threads |

Starting key map (**VERIFY each against the current version before it ships**):
- PCSX2 `PCSX2.ini`: `[EmuCore/GS] Renderer`, `upscale_multiplier`.
- RPCS3 `config.yml`: `Video: Renderer`, `Resolution Scale`, `Shader Mode`; `Core: SPU Decoder`.
- Dolphin `GFX.ini`: `[Settings] GFXBackend`, `InternalResolution`, `ShaderCompilationMode`; `Dolphin.ini [Core] CPUCore`.
- Lime3DS / Azahar `qt-config.ini`: `[Renderer] graphics_api`, `resolution_factor`, `async_shader_compilation`.
- Switch emulator: `Config.json` or `qt-config.ini`: backend, resolution scale, shader cache, texture recompression.
- Xenia `*.config.toml`: `[GPU] gpu`, resolution scale keys.
- PPSSPP `ppsspp.ini [Graphics]`: `GraphicsBackend`, `InternalResolution`.
Unknown emulator version: show the planned edits as a preview, apply only on confirmation.

### 5.4 Extras
- Learned per-game overrides: after a session, if the target FPS held, remember the preset; if not, step down one level (same ladder idea as Auto TDP).
- Handhelds: emulator sessions run under the existing Auto TDP governor and thermal guard.

## 6. Overlay and advisor

- Overlay: existing RTSS shared-memory OSD and ETW frame data, attached to the emulator's process tree (the emulator is tracked as the game process). No hook, no injection.
- Metrics: FPS, 1% low, frame time, GPU and CPU load, temperatures, watts (existing sensors).
- Advisor rule (data, evaluated by a pure Core function):
```json
{ "id": "ps2-async-shaders", "emulator": "pcsx2",
  "when": { "avgFpsBelowTargetPct": 90, "forSeconds": 20, "bound": "gpu|cpu|any" },
  "suggest": "Frame drops detected. Lower the internal resolution one step.",
  "edit": { "file": "PCSX2.ini", "section": "EmuCore/GS", "key": "upscale_multiplier", "step": -1 } }
```
- Bound inference: GPU load above 95% with CPU under 70% = GPU-bound; the reverse = CPU-bound; otherwise "any".
- Delivery: one toast at a time through the non-activating alert window, at most one per rule every 10 minutes, with "Apply" (runs the `edit` through §5.2, takes effect next launch), "Not now" and "Don't suggest this again".
- Learning: record accepted / ignored / helped per game and rule; stop repeating suggestions that did not help. The stutter detective and its fix buttons run on emulator sessions too.

## 7. UI

- **Window:** `EmulationView` in the main tab list. Left platform drawer (240 px, counts, collapses under 1100 px), centre poster grid (2:3 tiles, columns = floor(width / 200), minimum 2, virtualised, lazy images), right detail drawer (420 px).
- **Detail drawer:** hero banner, logo, title, platform, summary, rating, release date; buttons Play, Tune, Mods, Update; version and "update available" chip; last session stats.
- **Settings → Emulation:** emulator list with installed version, Update / Rollback / Channel, folders to scan, provider keys, "Test sources".
- **Toasts:** advisor and update notices (§6), non-activating.
- **Input:** keyboard and pad as in `.claude/rules/windows-ui.md`; LB/RB switch platform, A opens drawer, B closes.
- **Theme:** tokens only; light theme arrives with the existing backlog item.

## 8. Settings, storage, security

- New `LauncherSettings.Emulation`: `EmuRoot`, `ScanFolders`, `CheckIntervalHours`, `SteamGridDbKey`, `IgdbClientId`, `IgdbClientSecret`, `ScreenScraper*`, `GitHubToken` (all secrets DPAPI-encrypted).
- New library fields per game: `Platform`, `EmulatorId`, `RomPath`, `TitleId`, `Collections`, `TuningPreset`.
- Files under `%AppData%\FitzLauncher`: `emu-art\`, `emu-restore\`, `emu-versions.json`, `emulator-sources.json`.
- Process launch: `UseShellExecute = false`, `CreateNoWindow` where the emulator has a console, timeouts on helper tools. Background threads never throw out.

## 9. Decisions (answered)

1. Online version info: local detection plus emulator-native sources only (§4). No third-party catalogues.
2. Switch: Ryujinx fork (Ryubing) and Yuzu-style forks. Repos and asset patterns are chosen and verified at the Switch stage; until then their `source.type` is `manual` (user points at the exe).
3. N64: RMG and Project64 both.
4. Archives: `.zip` and `.7z` (SharpCompress; check licence before adding).
