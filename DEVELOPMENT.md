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

### Reveal phase triggers (HandleWatcher gone)
HandleWatcher used `NtQueryInformationProcess` + `DuplicateHandle` in a polling
loop on the game's handle table. EAC and similar anti-cheats kill the calling
process for that exact pattern — confirmed via FH6 terminating CM externally
~60s into a session with no .NET exception logged. Not fixable in user space;
the DuplicateHandle loop **is** the cheat signature.

Two reveal triggers, configurable per profile (`Profile.AcquisitionTrigger`):

1. **Timer mode** (default): each Reveal-After-Start device fires at its
   configured `DeviceRef.DelaySeconds` — an absolute `T+Xs` measured from the
   start of the reveal phase. Sub-second precision (it's a `double`). List
   order is reveal order; if a later device has a smaller time, it's clamped
   to the previous device's reveal time and fires right after. Default for a
   new Reveal-After-Start device is 5s.

2. **FirstDeviceOpened mode**: kernel ETW (`Microsoft-Windows-Kernel-File`)
   watches for the game's PID opening one of the profile's Always-Visible
   device files. The signal fires once, then the orchestrator holds for
   `Profile.PostAcquisitionDelaySeconds` (default 1.5s — see "Slot-commit
   grace period" below) before starting reveals. Per-device times are
   ignored in this mode; reveals fire back-to-back, paced only by IOCTL
   latency.

ETW is EAC-safe: it's one-way kernel telemetry, the consumer never touches
the source process. Anti-cheats use ETW themselves; it's not a cheat vector.

### Slot-commit grace period
Games that pick controller slot #1 from a HID scan don't commit the
assignment at the exact moment they first open a controller file. There's a
window of roughly 1–2 seconds where the game is observing what's visible and
batching candidate devices before locking in slot assignments. If we reveal
the other devices during that window, they're candidates too — and PnP
enumeration order, not arrival order, decides who wins.

Empirical (FH6 + MOZA): with the wheel becoming visible at T+10s, revealing
others at T+11s = pedals/handbrake got slot #1. Revealing at T+12s = wheel
got slot #1. So roughly 1.5s of "Always-Visible device alone" is required
before adding new devices.

`Profile.PostAcquisitionDelaySeconds` (default 1.5s) is exposed in the UI
under the acquisition-trigger dropdown when FirstDeviceOpened is selected.
In Timer mode the same effect is achieved by spacing the user-configured
T+Xs values themselves.

### FirstDeviceAcquisitionWatcher path matching
The watcher subscribes to `Microsoft-Windows-Kernel-File` via the TraceEvent
NuGet, filtered to `KernelTraceEventParser.Keywords.FileIOInit`. ETW reports
file names in NT object form (`\Device\HID00000XX`), not the Win32 symbolic
link form on `HidDevice.DeviceInterfacePath` (`\\?\HID#…#{guid}`).

`FirstDeviceAcquisitionWatcher.Start(pid, paths)` resolves each Win32 path to
its NT object name via `NtQueryObject(ObjectNameInformation)` at watch-start
time, then exact-matches incoming events against that set. Failed opens on
hidden devices have different paths and are ignored — no NtStatus correlation
needed. The acquisition event fires exactly once per session (`Interlocked`
guard on the worker thread).

ETW session name is `ControllerManager_DeviceAcquisition`; stale sessions
from prior crashes are stopped at Start. 30s timeout falls back to Timer
mode if no matching open is observed.

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

### Hide-list filter (session start)
The orchestrator enumerates with `showAllHid: true` — the broader HID list,
not just the strict gaming filter (`UsagePage 0x05` or `0x01/Usage 0x04/0x05`).
Sim peripherals routinely declare themselves with off-spec usages — SIMAGIC
handbrakes use Multi-Axis (`0x01/0x08`), some controllers use vendor-defined
pages — and the strict filter missed them. They'd stay visible at game scan
time and steal slot #1.

From the broad list, the orchestrator excludes:
- Keyboards (`HidDevice.IsKeyboardOrMouse`, UsagePage 1 / Usage 6)
- Mice (UsagePage 1 / Usage 2)
- Devices with no inputs (`AxisCount == 0 && ButtonCount == 0` — fans,
  audio devices, USB hubs reporting as HID; can't compete for a controller
  slot anyway)

`HidDevice` carries `UsagePage` and `Usage` populated by the enumerator so
this filter doesn't require re-opening HID descriptors at session start.

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
  + per-row T+Xs field (only meaningful for Reveal After Start AND only in
  Timer trigger mode — hidden in FirstDeviceOpened mode where times are
  ignored) + per-row Remove.
- First Reveal-After-Start device added auto-defaults to T+5s; users can edit.
- "Reveal trigger" dropdown selects Timer (default) or FirstDeviceOpened.
- "Wait after first device opened" field appears in FirstDeviceOpened mode
  only — drives `PostAcquisitionDelaySeconds`, default 1.5s.
- "Auto-trigger this profile when the game launches" checkbox drives
  `ProcessWatcherEnabled`.
- Save Profile button explicit. "Unsaved changes" indicator visible.
- Delete Profile requires confirmation. Per-profile scheduled task created by
  `ShortcutExporter` is cleaned up on delete via `LaunchTaskManager`.

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
| 0 (legacy) | `Profile.InitialDelaySeconds` + per-device "wait AFTER reveal" (int seconds) |
| 1 | per-device "wait BEFORE reveal" (relative) |
| 2 (current) | per-device absolute "reveal at T+X seconds from game launch" (double, sub-second precision) |

`ProfileEditorViewModel.LoadProfile` migrates v0 and v1 to v2 on read.
`ToProfile` always writes v2.

### Profile field reference (v2)

| Field | Type | Default | Notes |
|---|---|---|---|
| `id` | Guid | new | Stable identity across renames |
| `name` | string | "New Profile" | Display + shortcut/task naming |
| `gameExecutablePath` | string | "" | Launched by Dashboard/shortcut/Steam-wrap |
| `gameExecutableName` | string | "" | Process name for watcher matching |
| `schemaVersion` | int | 2 | Migration marker |
| `acquisitionTrigger` | enum | Timer | `Timer` or `FirstDeviceOpened` |
| `postAcquisitionDelaySeconds` | double | 1.5 | Grace period after ETW signal (FirstDeviceOpened only) |
| `processWatcherEnabled` | bool | true | Per-profile gate for the always-running watcher |
| `keepEnabled[]` | DeviceRef[] | [] | Always Visible devices |
| `disableThenRestore[]` | DeviceRef[] | [] | Reveal After Start, in order |
| `keepDisabled[]` | DeviceRef[] | [] | Always Hidden devices |

`DeviceRef`: `{ instanceId, deviceInterfacePath, friendlyName, delaySeconds }`.

### ProfileStore.Changed event
Fires after every successful Save. Subscribers should re-Load their in-memory
copy. `DashboardViewModel` subscribes to keep its profile dropdown in sync
with Games-tab add/delete/rename — both VMs would otherwise drift since they
each Load independently.

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

### Unit tests
There are none yet. The codebase has grown to the point where regressions in
the orchestrator timing, schema migration, or composite-HID expansion would
be hard to catch without automation. High-value areas to test first:

- **Profile schema migration** (`ProfileEditorViewModel.LoadProfile`): v0
  delays → v2 absolute times, v1 → v2 cumulative sum, v2 round-trips. All
  three cases have known input/output pairs from real profiles, easy to
  pin in tests.
- **Orchestrator reveal timing math**: target time clamping
  (`max(configured, lastRevealAtMs)`), absolute T+Xs semantics, sub-second
  precision. Don't need to mock HidHide — extract pure timing logic into
  a helper that returns `(deviceId, targetMs)` pairs given a profile +
  acquisition state. Test that.
- **HidDevice ChildInstanceIds expansion**
  (`LaunchOrchestrator.ExpandToChildren`): given a list of primary IDs +
  a device list with sibling children, returns all child IDs. Pure
  function, trivial to test.
- **DEVPKEY_Device_ContainerId grouping**: harder because it requires
  CfgMgr32. Could be tested by injecting a fake enumeration source if
  `DeviceEnumerator` is refactored to take a "raw HID enumeration"
  delegate.
- **NT path resolution in `FirstDeviceAcquisitionWatcher`**: skip — needs
  a real device handle.
- **AcquisitionTrigger flow in orchestrator**: refactor `WaitForFirstDeviceOpen`
  so the ETW watcher is injected (interface), then test the timer-fallback
  and grace-period logic without ETW.

Recommended setup: separate `app/Tests/ControllerManager.Tests.csproj`
using xUnit + FluentAssertions. Most of the testable logic is already in
pure-ish helpers; the orchestrator may need a small refactor to extract
timing from I/O.

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

- **FH6 end-to-end (Timer mode)**: confirm no EAC crash; FFB on the wheel;
  pedals/shifter reveal in order at the configured T+Xs times.
- **FH6 end-to-end (FirstDeviceOpened mode)**: same setup but with the
  acquisition trigger set. Wheel = Always Visible; others = Reveal After
  Start. Confirm log shows `[Acquisition] Game opened watched device:`
  followed by reveals 1.5s later. Wheel ends up at slot #1, others queue
  behind in profile order.
- **Slot-commit grace tuning**: if wheel still loses slot #1 in FH6, bump
  `Wait after first device opened` toward 2-3s. Document the working value
  per game.
- **Broad hide list**: confirm Logger output during `BeginGameSession`
  includes off-spec sim devices (SIMAGIC handbrake, etc.) in the hide list.
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
                       FirstDeviceAcquisitionWatcher (ETW), HidInputMonitor,
                       ProfileHealer, ShortcutExporter, TrayService, Logger
  /ViewModels        — MVVM view models (one per tab + per editor + per row)
  /Views             — XAML + code-behind
/tools/DeviceWatcher — Companion CLI for testing hide/show behavior end-to-end
/HidHide             — Reference HidHide source (forked from nefarius/HidHide)
```
