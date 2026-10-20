# AGENTS.md

Guidance for agents (and future contributors) working on this repo.

## What this is

**Lively Playlist** — a standalone Windows tray app that adds *playlist* functionality
to [Lively Wallpaper](https://github.com/lively-community/lively): curated groups of
wallpapers that rotate on a timer, per monitor.

Lively itself has **no** playlist feature (its `GetWallpapers` RPC returns only the
currently-active wallpaper, and the orphaned `TitlePlaylist` resource string is a
leftover from the old v1 UI). This app fills that gap **externally**, driving Lively
over its IPC API — it does not modify or fork Lively.

## Build & run

```powershell
# IMPORTANT: stop any running instance first, or the build fails with
# MSB3027/MSB3021 (the .exe is locked by the running process).
Get-Process -Name "LivelyPlaylist" -ErrorAction SilentlyContinue | Stop-Process -Force

& "$env:ProgramFiles\dotnet\dotnet.exe" build .\LivelyPlaylist.csproj -c Debug
& "$env:ProgramFiles\dotnet\dotnet.exe" run  --project .\LivelyPlaylist.csproj
```

- SDK: .NET 10, `net10.0-windows`, WinForms. `dotnet` is **not** on PATH — use the
  full path `C:\Program Files\dotnet\dotnet.exe`.
- Lively Wallpaper must be **running** (its core process hosts the IPC server) or the
  app warns at startup and the library scan finds nothing.
- Logs: `bin\Debug\net10.0-windows\lively-playlist.log` (also openable from the tray
  menu → "Open Log"). Config: `playlist.json` next to the exe, auto-reloaded on change.

## Architecture

```
LivelyPlaylist (this app, external process)
   │  gRPC over Windows NAMED PIPES (not TCP!)
   ▼
Lively.exe core process
```

- Transport: `GrpcDotNetNamedPipes` (`NamedPipeChannel(".", pipeName)`), **not** a TCP
  socket. A non-.NET client cannot easily talk to this — the app must be C#.
- Pipe name (mirrors `Lively.Common.Constants.SingleInstance`):
  `Grpc_LIVELY:DESKTOPWALLPAPERSYSTEM{Environment.UserName}`
- Proto files are **copied** into `Protos/` from the Lively source and codegen'd via
  `Grpc.Tools` (`GrpcServices="Client"`). We do not reference Lively's assemblies.
  Source of truth: https://github.com/lively-community/lively → `src/Lively/Lively.Grpc.Common/Proto/`.

### Key RPCs

| Service | RPC | Notes |
|---|---|---|
| `DesktopService` | `SetWallpaper(lively_info_path, monitor_id)` | `lively_info_path` = the wallpaper's **folder path** |
| `DesktopService` | `GetWallpapers()` | ⚠️ returns only **active** wallpapers — NOT the library |
| `DisplayService` | `GetScreens()` | monitors: `device_id`, `index`, `is_primary` |
| `SettingsService` | `GetSettings()` | includes `wallpaper_dir` (field 33) |

## Hard-won gotchas (do not re-learn these)

1. **Library discovery — do NOT use `GetWallpapers()`.** The server iterates
   `desktopCore.Wallpapers` = only what's on screen right now. The full library is on
   disk. `LivelyClient.GetLibraryAsync()` scans:
   - `{WallpaperDir}/wallpapers/`
   - `{WallpaperDir}/SaveData/wptmp/`

   each subfolder containing a `LivelyInfo.json`.

2. **`WallpaperDir` may be an empty string** in `Settings.json`. When empty, fall back
   to Lively's own default (from `SettingsModel.cs`):
   `{LocalAppData}\Lively Wallpaper\Library`. Without this fallback the scan silently
   finds 0 wallpapers.

3. **`LivelyInfo.json` `Type` is an integer**, not a string (enum: `1=web, 7=video,
   8=gif, 11=picture`…). Declaring it `string?` makes `JsonSerializer.Deserialize`
   throw — and an empty `catch` swallows it, producing "found 0 wallpapers" with no
   visible error. Always log parse failures (see `TryReadLivelyInfo`).

4. **Real titles/thumbnails** come from `LivelyInfo.json` (`Title`, `Thumbnail`),
   not from the folder name (`zmijuhra.1n3`). Thumbnail paths may be absolute or
   relative to the wallpaper folder — see `ResolveThumbnail`.

5. **Config changes must apply live.** Interval/mode go through
   `PlaylistEngine.ApplyLiveSettings()` (called on every control change). Persisting
   only on a Save click reads as "the timer is broken".

6. **DPI:** the app runs `SystemAware`. Fixed-pixel button heights clip text at >100%
   scaling — `MainForm` sets `AutoScaleMode.Dpi` + 96-DPI baseline. Never add vertical
   `Padding` to buttons (`StyleButton` keeps `Padding.Empty`); it makes text "sink".

7. **Debounce the config `FileSystemWatcher`** (~300ms) — editors fire multiple
   `Changed` events per save.

8. **Playback state must be event-driven, never cached in the UI.** The engine auto-starts
   on a background task (`Program.Main` → `Task.Run(StartAsync)`), so by the time
   `MainForm`/`TrayApp` render, `State` may already be `Running`. Reading `IsRunning`
   once at construction and writing labels imperatively caused a stale UI (button said
   "Start" while it was running → first click *paused* it → "two clicks to start").
   Now: engine exposes `PlaylistState` (`Stopped|Running|Paused`) + `StateChanged` event;
   UI has a single `RenderState(state)` that derives both the status label and the
   button text. **Always route state display through `RenderState`** — do not assign
   `_statusValue.Text` / `_toggleButton.Text` directly anywhere else.

9. **Distinguish the three states meaningfully:** `Stopped` = timer never started (or
   `Stop()` called), `Running` = timer ticking, `Paused` = timer halted but resumable.
   `ResumeAsync()` is the cheap path (just restarts the timer); `StartAsync()` does a
   full `RefreshAsync()` library re-scan. The toggle must pick
   `Paused → ResumeAsync`, `Stopped → StartAsync`, else a "Start" from Stopped would
   skip the library scan.

## Current architecture decisions

- **Config schema** (`PlaylistConfig`) — now MULTI-PLAYLIST:
  - **Shared** across playlists: `Monitors` (list of indices; empty = all),
    `PerMonitor` (bool), `StartWithWindows` (bool), `AutoStartWithLively` (bool).
  - **Per playlist** (`Playlists[]`, each a `PlaylistDefinition`): `Id` (guid),
    `Name`, `IntervalSeconds`, `Mode` (Sequential|Shuffle), `Wallpapers` (folder
    paths; **empty = all**).
  - `ActivePlaylistId` points at the playing one; `ActivePlaylist` is a computed
    `[JsonIgnore]` lookup (falls back to the first playlist).
  - **Legacy migration:** a config with no `Playlists` array is auto-migrated on
    load (flat fields → a single "Default" playlist) and re-saved once. See
    `PlaylistConfig.Load`/`MigrateLegacy`/`EnsureValid`.
  - UI keeps monitor targeting intentionally global (agreed with user); the
    playlist dropdown applies immediately on switch.
- **Threading:** `SemaphoreSlim` (not `lock`) guards `AdvanceAsync` because it awaits
  inside the critical section. Engine events (`StatusChanged`, `WallpaperChanged`,
  `StateChanged`) can fire on timer threads — UI must marshal via `SafeInvoke`/`BeginInvoke`.
- **Playback state:** `PlaylistState` enum + `StateChanged` event on the engine; the UI
  renders through `MainForm.RenderState()` and the tray mirrors it. See gotcha #8.
- **UI:** dark theme centralized in `DarkTheme.cs` (palette + button/caption styler).
  Tray menu uses a custom `DarkMenuRenderer`. The window hides on close so the tray
  stays alive.
- **Rotation:** shared sequence with per-monitor offset (`_sequenceIndex + monitor.Index`)
  so screens differ; `PerMonitor=true` gives independent *random* picks per screen.
  **Shuffle** uses a Fisher-Yates *shuffle bag* (drawn without replacement) so a
  wallpaper never repeats until all have played — plain `Random.Next` repeated too
  often (user-reported bug).
- **Startup behavior** (see `Program.cs` + `StartupManager.cs`):
  - The app **always launches minimized to tray** — `Main()` never shows the window;
    it's opened via tray double-click or "Open Control Panel".
  - It **no longer exits** when Lively isn't running. It idles in the tray, polls the
    named pipe (`LivelyClient.WaitForCoreAsync`, 2s), and shows a balloon tip.
  - `StartWithWindows` writes/removes an **HKCU** Run key (no admin) — synced on every
    launch in `Program.SyncStartupRegistration`.
  - `AutoStartWithLively` = start the playlist automatically once the pipe appears;
    otherwise it just notifies "Lively detected, press Start".
- **Library auto-detect:** the engine watches both library folders with
  `FileSystemWatcher` (folder create/delete/rename, debounced ~700ms) and raises
  `LibraryChanged`; `MainForm` refreshes the picker on that event. Folder-level only —
  a file edit *inside* an existing wallpaper folder won't retrigger (use Reload Config).

## Known gaps / pending work

- **Per-monitor playlists** — user wants an independent playlist per display (Lively's
  own UI allows per-display wallpaper assignment). Requires a config-schema change
  (e.g. a per-monitor list) + UI section. Not yet designed — scope before building.
- **`Monitors` / `PerMonitor`** exist in config but have no UI controls yet.
- **Repo publish prep** — ✅ **done**: `.gitignore`, `README.md`, `LICENSE` (MIT),
  `.editorconfig` all in place. Secrets/personal-path scan clean (no `C:\Users\...` or
  credentials in tracked source; `.commandcode/` and `bin/`/`obj/` are gitignored).
  **`git init`/commit NOT done yet — confirm with the user first.** Note: `LICENSE`
  copyright line reads "Lively Playlist contributors" — user should swap in their
  preferred holder name.
- Playlist ordering: currently `Wallpapers` order is respected by `ResolvePool`, but
  the picker UI doesn't offer drag-to-reorder.

### Discussed feature designs (scoping notes)

**1. Multiple playlists — ✅ BUILT.** Config is now multi-playlist (see "Config schema"
above). UI has a playlist dropdown + New/Rename/Delete; the dropdown applies on switch.
Decisions that were settled with the user:
- Monitor settings (`Monitors`, `PerMonitor`) stay **shared**, not per-playlist.
- Deleting the **active** playlist **stops playback** (engine `Stop()`), then selects
  the first remaining playlist.
- Switching a playlist in the dropdown **applies immediately** (no separate confirm).
- The "Save Playlist" button writes the picker selection to the **active** playlist.

**2. Grid picker with embedded thumbnail previews (doable; caveat = library scale).**
- Two WinForms options:
  - `ListView` `LargeIcon` + `ImageList`: quick, but **eager-loads every thumbnail**
    into memory.
  - `FlowLayoutPanel` of custom cards (thumbnail + title + checkbox): prettier, lazy
    loads, but more objects and we already know thumbnail disposal matters here.
- ~13 wallpapers → either is fine. 200+ → WinForms scrolls poorly (no virtualization):
  downscale thumbnails on load, add a placeholder (some `LivelyInfo.json` have no
  `Thumbnail`). Consider an `ImageList` size cap (~256px).

**3. Porting to Lively upstream — engine ports, UI does NOT.**
- **Lively's UI is WinUI 3, not WinForms** — the entire current UI (dark theme, checked
  list, buttons) would be a from-scratch XAML rewrite, not a copy.
- What ports well: engine logic (sequencing, per-monitor offset, shuffle, config
  handling) — it's pure C# with no UI coupling. Inside Lively it wouldn't need the
  gRPC hop at all.
- Grid gets *easier* upstream: Lively's library view already renders thumbnail tiles,
  so reuse those components instead of rebuilding in WinForms.
- Orphaned `TitlePlaylist` resource string hints playlist UI was once planned upstream.
- **Recommendation (agreed):** build here first, keep the engine UI-free so it stays
  portable; only attempt the WinUI port as a separately-scoped effort (MVVM, large
  codebase, upstream is refactoring — see the `core-separation` branch).

**General advice:** keep `PlaylistEngine` free of WinForms types so features 1 & 3 stay
portable. UI features can then evolve independently.

## Repo layout

```
LivelyPlaylist.csproj   # net10.0-windows, Grpc.Tools codegen for Protos/*.proto
                        #   ApplicationIcon + EmbeddedResource: Assets\LivelyPlaylist.ico
Program.cs              # entry: start-to-tray, wait-for-Lively, sync Run key
LivelyClient.cs         # gRPC wrapper + ON-DISK library scan (the tricky part)
PlaylistEngine.cs       # timer, rotation (shuffle bag), config/library watchers, live settings
PlaylistConfig.cs       # multi-playlist JSON load/save + legacy migration
MainForm.cs             # dark control window: preview, picker, playlist mgmt, startup opts
TrayApp.cs              # tray icon + dark context menu + playlist submenu + DarkMenuRenderer
DarkTheme.cs            # palette + button/label styling
AppIcon.cs              # loads the .ico from an embedded resource (single-file safe)
StartupManager.cs       # HKCU Run key (start with Windows)
Logger.cs               # file logger (lively-playlist.log)
Assets/                 # LivelyPlaylist.ico (7 sizes: 16..256) — swap in your own design here
Protos/                 # copied from Lively repo (wallpaper, display, commands, settings)
```

### App icon

- Source: `Assets\LivelyPlaylist.ico` (multi-size, 32-bit PNG-compressed entries).
- Wired in two places: `<ApplicationIcon>` in the csproj (exe/title bar) and an
  `EmbeddedResource` loaded by `AppIcon.cs` for the tray + form — **embedded, not a
  file path**, because single-file publish doesn't extract loose files at runtime.
- Current icon: **"Glossy Rainbow Flower Refresh Icon.png"** (user-supplied art in
  `Assets/`), converted to the `.ico`. To change it again: overwrite
  `Assets\LivelyPlaylist.ico` from a new PNG (7 sizes: 16,24,32,48,64,128,256 — use
  `Convert-PngToIco.ps1` from the session scratchpad) and rebuild — no code changes.
- **Verification gotcha:** GDI+'s `Icon.ToBitmap()` renders PNG-frame icons as garbage —
  that does NOT mean the `.ico` is broken. Verify with `DrawIconEx` (Windows' decoder,
  same as Explorer/tray) instead, as done in `VerifyIcons.ps1`.
- **Changing the icon requires a CLEAN rebuild** (`dotnet build` reports "up-to-date"
  and does NOT re-embed a changed `.ico` — MSBuild doesn't track it as an input). Delete
  `bin/` and `obj/` first, or the exes keep the old icon baked in. Even then, **Explorer's
  icon cache** may keep showing the old art until the cache is rebuilt (log off/on, or
  `ie4uinit.exe -show`) — the file bytes are correct regardless.
