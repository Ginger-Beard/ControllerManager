# Controller Manager — Developer Documentation

This is the doc to read first when continuing development. It captures what the
app does, why each load-bearing decision is made the way it is, what the open
work is, and how to build and test.

User-facing docs live in [README.md](README.md). When user-visible behavior
changes, both files need a sweep.

---

## What the app is

A per-game HID device profile manager for sim racing and controller games. Each
profile is an ordered list of devices, each assigned one of three roles:

- **Always Visible** — the game sees this from launch.
- **Reveal After Start** — hidden at launch, revealed at a configured absolute
  T+X seconds after the game process starts. List order is the reveal order;
  whichever device is revealed first claims the next available controller slot.
- **Always Hidden** — never visible to this game.

Devices not in the profile are hidden from the game for the duration of the
session. Other processes (companion apps, dashboards, etc.) keep full access
unconditionally — there's no whitelist for users to maintain.

Hiding is implemented by [HidHide](https://github.com/nefarius/HidHide), a
signed kernel filter driver. We act as a client to its IOCTLs.

---

## Architectural ground truth

### FFB / slot-ordering fix
Forza Horizon games and many other titles assign FFB and slot #1 based on which
gaming HID device is visible to DirectInput first at startup. HidHide hooks
`PsSetLoadImageNotifyRoutine`, so deny-list rules are in effect *before any game
code runs* — no race window. The game's startup scan only sees Always-Visible
devices; the reveal phase brings the rest online afterward.

### Reveal phase is timer-based (HandleWatcher gone)
HandleWatcher used `NtQueryInformationProcess` + `DuplicateHandle` in a polling
loop on the game's handle table. EAC and similar anti-cheats kill the calling
process for that exact pattern — confirmed via FH6 terminating CM externally
~60s into a session with no .NET exception logged. Not fixable in user space;
the DuplicateHandle loop **is** the cheat signature.

Reveal timing now uses a fixed absolute `T+Xs` value per device, stored in
`DeviceRef.DelaySeconds`. Default for a new Reveal-After-Start device is 5s
(enough for FH-style startup scans to commit slot #1 to the wheel). List order
is the reveal order; if a later device has a smaller time, it's clamped to the
previous device's reveal time and fires right after.

### Reveal phase limit
Works only for hot-plug-aware games (WGI, RawInput, modern XInput). Pure legacy
DirectInput-only games that do one startup scan and never re-enumerate will see
FFB *or* late-revealed devices, not both — HidHide doesn't fire
`DBT_DEVICEARRIVAL` when a device leaves the session blacklist, so a
non-listening game won't notice. Documented in the README; no workaround.

### EAC / anti-cheat safety
Beyond removing HandleWatcher, one remaining touch of the game process is
`HidHideClient.UpdateSessionGameNtPath`: it opens the game with
`PROCESS_QUERY_LIMITED_INFORMATION` (same right Task Manager uses) to read its
NT image path for the deny list. Scoped to only run when `Win32ToNtPath` failed
— i.e. for UNC/WSL paths only. Normal `C:\...` launches don't touch the game
process at all.

### HidHide driver compatibility — 1.4.181.0
The stock signed driver doesn't ship the session-blacklist IOCTLs
(`0x80016020/24`); those exist only in the modified source under `HidHide/` in
this repo. We work around by snapshot-and-restore of the persistent blacklist
around each session — see `HidHideClient.BeginGameSession` /
`UpdateSessionBlacklist` / `EndGameSession`. If CM crashes mid-session, session
devices remain in the persistent BL until the user re-enables them in the
Devices tab.

### Composite HID grouping
Devices that expose multiple HID interfaces (composite MI_NN children) share a
Windows-assigned `DEVPKEY_Device_ContainerId` GUID. `DeviceEnumerator` groups
HID children by ContainerId — same approach HidHide's own client uses. The
"primary" displayed interface is whichever child is a gaming HID; siblings
ride along in `HidDevice.ChildInstanceIds`. Every site that writes to the
blacklist (Devices-tab toggle, `LaunchOrchestrator.HideDevices`,
`RevealDisableThenRestore`, `SteamWrapInvocation`) expands to all siblings
because HidHide's kernel filter does direct string compare with no ancestor
traversal.

### Four supported use cases (settled)
1. **Forza Horizon / Xbox sim racing** — wheel = AlwaysVisible,
   pedals/shifter = RevealAfterStart at staggered T+Xs times,
   gamepad = AlwaysHidden.
2. **Sim racing companion apps** (SimHub, Pit House, GHub, Synapse) —
   automatically retain device access via HidHide inverse-whitelist mode (only
   the game's exe is in the deny list during a session). No user config.
3. **Other PC games with controllers** — gamepad = AlwaysVisible, sim rig =
   AlwaysHidden. No RevealAfterStart entries; orchestrator skips wait+reveal.
4. **Sunshine / Apollo streaming** — virtual gamepad = AlwaysVisible (build the
   profile while a remote session is active so the dynamic device appears in
   the picker), everything else = AlwaysHidden.

---

## Behavioral rules

These are non-negotiable UX guarantees. Code changes that break any of them
need explicit user approval and a doc update.

### Default state — no game running
- `HidHideClient.ApplyState` keeps the filter `Active = false` when the
  persistent blacklist is empty. All processes see all devices.
- `Active = true` only when something is in the persistent blacklist (Devices
  tab toggle has hidden something) or a session is running.
- HidHide's `Active` state is managed automatically; there is no manual master
  toggle in the UI. (The toggle was removed — users were confused by it and
  it shouldn't be a thing they have to manage.)

### Companion apps
- **Zero whitelist configuration required, ever.**
- During a game session: HidHide is in inverse-whitelist mode with only the
  game's exe in the deny list. Every other process — SimHub, Pit House, GHub,
  Synapse, joy.cpl, anything — keeps full access automatically. No allow-list
  to maintain.
- Users must never be asked "which apps should still see your devices."

### Game session
- On launch: session devices written to persistent BL; pre-session BL
  snapshotted for restore; game exe path added to inverse whitelist;
  `Active = true`.
- During: HidHide enforces; orchestrator times the reveals.
- On exit (normal or abnormal): pre-session BL restored verbatim; inverse
  whitelist cleared; `ApplyState` recomputes Active.

### Profile editor (current UI shape)
- One ordered device list per profile. Drag handle (`☰`) to reorder; arrows
  for keyboard-accessible one-step moves. Drag has a ghost adorner and a green
  insertion line.
- Per-row role selector (Always Visible / Reveal After Start / Always Hidden)
  + per-row T+Xs field (only meaningful for Reveal After Start) + per-row
  Remove.
- First Reveal-After-Start device added auto-defaults to T+5s; users can edit.
- Save Profile button explicit. "Unsaved changes" indicator visible.
- Delete Profile requires confirmation.

### Devices tab
- Banner: "Devices turned off here are hidden from your entire computer. Use
  game profiles (Games tab) to hide devices only while a specific game runs."
- On/off toggle per row affects the persistent blacklist.
- Composite devices: toggling one row hides every HID interface that shares
  its ContainerId (see Composite HID grouping above).
- Show All Devices toggle hides keyboards/mice/audio by default; turn on for
  diagnostic view.
- Inputs column is hidden by default — `InputSummary` property kept on the
  model in case a details panel surfaces it later.

### Launch paths and UAC
- **Dashboard Launch button** — no UAC (CM is already elevated, just calls
  the orchestrator).
- **Desktop / Start Menu shortcuts** — no UAC; `ShortcutExporter` creates a
  per-profile Scheduled Task via `LaunchTaskManager` and points the .lnk at
  `schtasks /Run /TN ...`. Same trick as Start-with-Windows.
- **Process watcher** — no UAC (CM is already elevated). Polls every 500ms;
  has a 0–500ms race window before the orchestrator hides devices, which can
  matter for FFB-sensitive games. Per-profile gate via
  `Profile.ProcessWatcherEnabled`; service always runs (no global toggle in
  Settings).
- **Steam Launch Options (`--steam-wrap`)** — currently UAC-prompts on every
  launch. See "Open work — Steam launch UAC" below.

### Launch behavior
- When launched at boot via the Start-with-Windows scheduled task (argument
  `--startup`), CM honors the "Start minimized to tray" setting.
- When launched any other way (manual double-click, Start menu, Dashboard
  re-entry, second-instance forward), CM always shows the window — `--startup`
  is the only signal for tray-only startup.

---

## Reference

### IOCTL surface (HidHide)
- Control device: `\\.\HidHide`, open with `GENERIC_READ`
- Formula: `CTL_CODE(32769, f, METHOD_BUFFERED, FILE_READ_DATA)`
  = `0x80014000 | (f << 2)`

| IOCTL | Code | Notes |
|---|---|---|
| GET/SET_WHITELIST | 0x80016000 / 04 | NT device paths, multi-string |
| GET/SET_BLACKLIST | 0x80016008 / 0C | Device instance paths, multi-string |
| GET/SET_ACTIVE    | 0x80016010 / 14 | BOOLEAN |
| GET/SET_WLINVERSE | 0x80016018 / 1C | BOOLEAN; true = whitelist acts as deny-list |

Session blacklist IOCTLs (`0x80016020/24`) are documented in `HidHide/` but
NOT in the stock signed driver — see HidHide driver compatibility above.

### Profile schema versions
| Version | Semantics of `DeviceRef.DelaySeconds` |
|---|---|
| 0 (legacy) | `Profile.InitialDelaySeconds` + per-device "wait AFTER reveal" |
| 1 | per-device "wait BEFORE reveal" (relative) |
| 2 (current) | per-device absolute "reveal at T+X seconds from game launch" |

`ProfileEditorViewModel.LoadProfile` migrates v0 and v1 to v2 on read.
`ToProfile` always writes v2.

### IPC operations
`IpcServer` listens on named pipe `ControllerManager`. Second-instance
forwards:

| Op | Payload | Behavior |
|---|---|---|
| `launch` | `[profileId]` | Start orchestrator for the named profile |
| `steam-wrap` | `[profileId, ...]` | Run `SteamWrapInvocation.HandleAsync` |
| `show` | none | `Tray.ShowWindow()` — bring window to front |

---

## Open work

### Steam launch UAC (deferred — process watcher covers 99% of cases)

**Current state**: Process watcher polls every 500ms for configured game
executables and triggers profiles automatically. For most users this is enough
— they launch the game however they want (Steam, shortcut, Start menu) and CM
catches it before the device-enumeration window matters.

**Race window**: ~0–500ms gap between game spawn and CM's hide call. FH-style
games do their DirectInput slot assignment in the first ~30ms, so the watcher
will sometimes miss the window. For those games, users can use the Dashboard
Launch button (no race) or the `.lnk` shortcut (no race, no UAC since
`LaunchTaskManager`). The Steam wrapper is the only remaining path that always
prompts UAC.

**If/when this becomes a real problem**, the fix is a binary split:

1. Keep `ControllerManager.exe` (`requireAdministrator`). Tray + HidHide IOCTLs
   as today.
2. Add `ControllerManagerLauncher.exe` — no admin manifest, never UAC-prompts.
   Tiny console exe (~100 lines):
   - Parse `--steam-wrap <profileId> -- <gameArgs>`
   - IPC the tray: "begin session for profile X" (tray does the elevated HidHide work)
   - `Process.Start(gameExe, gameArgs)` — game runs as user, non-admin (good for EAC)
   - `WaitForExitAsync` on the game (keeps Steam playtime tracking)
   - IPC tray: "end session"
   - Exit with the game's exit code

Steam Launch Options would change to point at the launcher. Requires the tray
to be already running; if not, launcher errors with "open Controller Manager
or enable Start with Windows."

Touchpoints: new `app/Launcher/ControllerManagerLauncher.csproj`, two IPC ops
on `IpcServer` (`begin-steam-session` / `end-steam-session`) doing the work
`SteamWrapInvocation.HandleAsync` does today minus the spawn, update
`GamesViewModel.CopySteamCommandCommand` to emit the launcher path, update the
release workflow to bundle both exes. ~2 hours.

### Real icon
`app/app.ico` is a placeholder. Replace with a real multi-size icon (16 / 32 /
48 / 256). Direction: device-manager-like, since the tool is framed as a
controller-focused Device Manager.

### Code signing
Apply to [SignPath.io](https://signpath.io/product/open-source) (free for OSS,
GitHub Actions integration, multi-day approval). Microsoft Trusted Signing
(~$10/mo on Azure) is the paid alternative. Until signed, SmartScreen warns on
first run.

---

## Testing checklist (no code; before each release)

- **FH6 end-to-end**: confirm no EAC crash with the timer-based reveal; FFB on
  the wheel; pedals/shifter reveal in order at the configured T+Xs times.
- **Sunshine / Apollo**: build a profile while a remote session is active;
  verify the virtual gamepad shows up in the picker; verify physical
  controllers stay hidden during the stream.
- **Profile ID healing**: cause a device's instance ID to change (USB reseat,
  port change). Reopen the profile in the Games tab — `ProfileHealer` should
  rewrite it silently and show the orange status banner.
- **Devices tab toggle**: verify on/off persists across app restarts; verify
  the BL is cleared cleanly on `Hide all → ON, then OFF` cycle.
- **Composite HID**: with a device that exposes multiple HID interfaces (G29,
  G920, certain Xbox controllers), toggle off in the Devices tab and confirm
  no sibling interface remains accessible.
- **Drag-drop reorder**: in the Games-tab profile editor, drag a row and
  confirm the green insertion line + ghost adorner appear.
- **Input monitor calibration**: hover over an axis or stick → press Calibrate
  → leave at rest for 5s → confirm the deadzone band/circle renders and the
  status text recommends a percentage.
- **Shortcut UAC-free launch**: export a Desktop shortcut for a profile,
  double-click — confirm no UAC prompt.

---

## Build notes

- Build: `dotnet.exe build` from `/app` (Windows binary, not WSL `dotnet`)
- Self-contained publish:
  `dotnet.exe publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish-sc`
- Slim publish:
  `dotnet.exe publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -o publish-fd`
- Release: push a `vX.Y.Z` tag to trigger GitHub Actions (builds both).

### Project layout
```
/app                 — WPF app (.NET 10, MVVM)
  /Cli               — Command-line entry points (LaunchInvocation, SteamWrapInvocation)
  /Models            — Profile, HidDevice, AppSettings, etc.
  /Native            — P/Invoke wrappers (HidApi, NtDll, SetupApi)
  /Services          — Orchestrator, HidHideClient, DeviceEnumerator, ProfileStore,
                       SettingsStore, IpcServer, LaunchTaskManager, ProcessWatcher,
                       HidInputMonitor, ProfileHealer, ShortcutExporter, TrayService,
                       Logger
  /ViewModels        — MVVM view models (one per tab + per editor + per row)
  /Views             — XAML + code-behind
/tools/DeviceWatcher — Companion CLI for testing hide/show behavior end-to-end
/HidHide             — Reference HidHide source (forked from nefarius/HidHide)
```
