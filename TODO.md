# TODO — Controller Manager

Reference this at the start of any session continuing work on this project.
Also reference **CRITERIA.md** for agreed UX requirements on the HidHide integration.

---

## What this tool is

A per-game HID device profile manager for sim racing and controller games. Profiles define which devices to disable before launch, re-enable as the game picks them up (staggered via handle watching), or keep disabled for the whole session.

Three device roles per profile:
- **Keep Enabled** — never touch (wheel base)
- **Disable → Re-enable** — disable before launch, re-enable one-by-one as game opens HID/DirectInput handles
- **Keep Disabled** — disable for whole session, re-enable on game exit

---

## Root cause (confirmed)

FH6/FH5 read DirectInput calibration registry keys at startup for every connected device simultaneously (~30ms burst). The fix: disable interfering devices before launch. MOZA in Forza Compatibility Mode (presents as Fanatec VID 0x0EB7) gets FFB when it's the only device visible.

HandleWatcher detects this by watching the game process for `\REGISTRY\...\DirectInput\VID_*` handles (FH6/DInput games) and `\Device\HID*` handles (RawInput/WGI games).

---

## Launch paths

1. **Steam wrapper** — `"ControllerManager.exe" --steam-wrap <profileId> -- %command%` in Launch Options
2. **Shortcut** — `.lnk` pointing to `ControllerManager.exe --launch <profileId>`
3. **In-app Launch button** — Dashboard tab
4. **Process watcher** — background safety net

---

## Recent decisions (2026-05-19)

### HandleWatcher removal — EAC incompatibility (resolved)

HandleWatcher polls a target process's handle table via `NtQueryInformationProcess`
(class 51) + `DuplicateHandle` on every entry. This is the exact pattern memory cheats
use, so anti-cheats (EAC confirmed via FH6 crash; BattlEye assumed identical) terminate
the calling process externally. Symptom: app vanishes ~60s into the session with no
.NET exception logged. Not fixable in user space — the DuplicateHandle loop **is** the
signature.

**Decision**: kill HandleWatcher entirely. Replace `WaitForAcquisition` with a fixed
timer (`Profile.InitialDelaySeconds`, default **5s** for new profiles, configurable
up to ~30s for slow-loading games).

### Why a timer is sufficient

HandleWatcher's role was always timing, not ordering. The actual FFB fix is "hide
everything except the wheel before game launch" — that happens before any game code
runs (HidHide hooks `PsSetLoadImageNotifyRoutine`) and has no dependency on
HandleWatcher.

A 5s timer waits for the game's startup DirectInput scan to commit slot #1 to the
wheel. After that, the orchestrator reveals devices one at a time with per-device
`DelaySeconds` spacing. Since the `RevealAfterStart` list is ordered, slot assignment
is stable across launches (wheel=1, pedals=2, shifter=3, etc.) — the game's hot-plug
listener processes arrivals in order.

### Four supported use cases (architecturally settled)

1. **Forza Horizon / Xbox sim racing** — wheel = AlwaysVisible, pedals/shifter =
   RevealAfterStart in mapping order, gamepad = AlwaysHidden. Timer-based reveal
   preserves FFB *and* stable slot ordering.
2. **Sim racing companion apps** (SimHub, Pit House, GHub, Synapse) — automatically
   retain full device access via HidHide inverse-whitelist mode (only the game's exe
   sits in the deny list during a session). No allow-list configuration required.
3. **Other PC games with controllers** — gamepad = AlwaysVisible, everything else =
   AlwaysHidden. No RevealAfterStart entries → orchestrator skips wait+reveal entirely.
4. **Sunshine / Apollo streaming** — virtual gamepad = AlwaysVisible (build the profile
   while a session is active so the dynamic device shows up), everything else =
   AlwaysHidden. Same shape as case 3.

### The honest reveal-phase limit (document in README)

Reveal-after-start relies on the game being hot-plug aware (WGI / RawInput /
modern XInput). For pure legacy DirectInput-only games that do a one-shot
startup scan and never re-enumerate, you cannot have both FFB *and* late-revealed
devices via HidHide — pick one. (The old pnputil backend had the same limit since
`DBT_DEVICEARRIVAL` doesn't help if the game isn't listening.) Worth calling out
honestly in README rather than implying universal support.

### UpdateSessionGameNtPath safety for anti-cheat games

The one remaining place we touch the game process during a session is
`UpdateSessionGameNtPath` (used for WSL/UNC path correction). It calls
`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW` —
same access right Task Manager uses, generally allowed by anti-cheats. Scope it to
only run when the original Win32→NT conversion failed (i.e., path wasn't a normal
`X:\...` drive) so EAC games never get touched at all.

### HidHide driver version compatibility

Installed driver on this dev machine: **1.4.181.0**. Verified via `strings` dump —
this version does NOT export `OnControlDeviceIoAddSessionBlacklist` /
`OnControlDeviceIoClearSessionBlacklist`. Those IOCTLs (0x80016020/24) exist only in
the modified source in our `HidHide/` repo. Every `AddSessionBlacklist` call against
the stock driver silently fails, which is why session-time hiding initially appeared
not to work end-to-end.

**Workaround in place**: use the persistent blacklist for session-time hiding.
`BeginGameSession` snapshots the pre-session BL, writes session devices into the
persistent BL for the duration, and `EndGameSession` restores the snapshot verbatim.
`UpdateSessionBlacklist` applies a delta to the same persistent BL.

Trade-off: if CM crashes mid-session, session devices remain in the persistent BL.
The user will see them as disabled in the Devices tab on next launch and can
re-enable manually. Acceptable for now. If we later want true session-isolated
hiding (independent of persistent state during a session, like the modified driver's
design intent), we'd need a signed driver update via Nefarius or our own signed fork
— not worth doing now.

---

## Code review findings (2026-05-19) ✅ (applied)

Done in this pass:
- HandleWatcher removed entirely (files + orchestrator references)
- Orchestrator phase reduced to a timer-based `WaitBeforeReveal(InitialDelaySeconds)`
- Single shared `LaunchOrchestrator` via `App.Orchestrator`; `DashboardViewModel` no
  longer disposes it (App owns it)
- `UpdateSessionGameNtPath` short-circuits when `Win32ToNtPath` already produced a
  `\Device\...` path — no `OpenProcess` against the game for normal C:\ paths
- `Profile.InitialDelaySeconds` default raised from 0 → 5 (FFB-sensitive games get a
  sensible wait window out of the box)
- Dead code removed: `HandleWatcher.cs`, `HandleWatcherViewModel.cs`,
  `HandleWatcherEntry.cs`, `HidHandleEvent.cs`, `WaitForAnyDeviceEvent`,
  `WaitForDeviceEvent`, `ParseVidPid`, `VidPidRx`, session-BL IOCTL constants,
  `DevicesViewModel.HidHideActive`, `Profile.HotkeyBinding`,
  `Profile.HandleWatcherStepTimeoutMs`, `_handleWatcherStepTimeoutMs`
- `AddToPersistentBlacklist` skips broadcast on no-op
- `LaunchOrchestrator.RunFlow` no longer logs the legacy `trigger=` field
- CRITERIA.md updated: pnputil fallback section removed; Profile-UI section rewritten
  to describe the actual current single-ordered-list model

Still kept (intentionally, for backwards-compatible JSON read):
- `Profile.TriggerMode` and `Profile.TimerSeconds` — migration path on load. Not
  written by the editor anymore. Drop after one release cycle.

### Composite HID device handling (open, edge case)
A Devices-tab row that deduplicates two HID interfaces (e.g. MI_00 + MI_01) into one
display row only adds the deduped instance ID to the blacklist when toggled off. Other
child interface paths remain accessible. Needs `HidDevice` to track the full set of
interface instance IDs so `ToggleEnabled` can blacklist all of them. Not blocking
sim racing use cases (most game controllers expose one interface) — defer until a real
device hits this.

---

## MVP — all complete ✅

1. ✅ WPF project scaffold (.NET 10, requireAdministrator, MVVM)
2. ✅ Device enable/disable (PowerShell Enable/Disable-PnpDevice)
3. ✅ Device enumeration + Devices tab (WMI, VID/PID, BusReportedName, ON/OFF toggle)
4. ✅ Failsafe state.json (write before disable, clear after enable, recover on startup)
5. ✅ Profile model + persistence (three device lists, TriggerMode, games tab editor)
6. ✅ HandleWatcher (NtQueryInformationProcess, \Device\HID* + \DirectInput\ handles)
7. ✅ LaunchOrchestrator state machine (Idle→Disable→Launch→Acquire→Restore→Monitor)
8. ✅ Dashboard tab (profile picker, device summary lists, Launch/Restore, activity log)
9. ✅ Steam wrapper (--steam-wrap CLI, disable→spawn game→wait→restore)
10. ✅ Process watcher (500ms poll, auto-triggers on game launch)
11. ✅ Shortcut export (WScript.Shell .lnk, Desktop + Start Menu buttons in Games tab)
12. ✅ Single-instance + named pipe IPC (mutex, \\.\pipe\ControllerManager, --launch forwarding)
13. ✅ Settings tab (Start with Windows, process watcher toggle, logging level, pin to top)
14. ✅ System tray icon + per-profile quick-launch from tray
15. ✅ File logging with Off/Normal/Verbose levels

---

## Backlog

### UAC / Steam integration
- Steam command triggers a UAC prompt on every launch because ControllerManager.exe has
  `requireAdministrator` in its manifest. If Controller Manager is already running in the tray,
  the second instance still needs to elevate to forward IPC, then exits — still prompts.
  Fix: create a Scheduled Task set to "Run with highest privileges" and have the Steam
  command trigger the task instead of the exe directly. No UAC prompt if user is already
  an admin.

### Documentation
- Rewrite README as user-facing setup guide (not internal docs):
  - Lead with what it does and why in plain language
  - Step-by-step setup with screenshots
  - Real game examples (FH5/FH6 confirmed, others clearly marked unverified)
  - Explain the two download options: slim (needs .NET runtime) vs self-contained
    (bigger file, runs anywhere) — most users should grab self-contained
  - Remove any hallucinated or unverified game examples
  - Include Sunshine/Apollo streaming example (see below)

### Sunshine / Apollo streaming example (for docs)
Use case: when game streaming via Sunshine or Apollo, the host creates a virtual controller
(via ViGEm) for the remote client's input. If physical controllers are also present, the
game may assign them lower slot numbers, pushing the virtual controller off slot #1.
Profile setup:
  - Keep Disabled: all physical game controllers on the host
  - Keep Enabled: the Sunshine/Apollo virtual controller (appears in Devices tab when a
    session is active — find its VID/PID there and add it to the profile)
  - The virtual controller is only present while a session is active, so build the profile
    with a client connected
Trigger setup: use Sunshine's "Command Preparations" in its web UI — put
`"C:\path\to\ControllerManager.exe" --launch <profileId>` in the cmd (blocking) field so
Controller Manager disables physical controllers before the game launches. On app exit, Sunshine
has a "Detach Command" field — put the same `--launch` there or rely on process watcher
to re-enable on game exit. Works the same way for Apollo (same web UI structure as a
Sunshine fork).

### Profile device ID healing ✅
- Needs testing

### Icon
- Current icon is placeholder. Need a real icon — suggest something with a
  joystick/controller and a reorder/sort visual. Can use Figma or commission.
  Replace `app/app.ico` (must be .ico format, ideally multi-size: 16/32/48/256px).

### Code signing
- Apply to SignPath.io (free for OSS) — legitimate Authenticode signature, integrates with
  GitHub Actions. Takes a few days to approve. See signpath.io/product/open-source
- Alternative: Microsoft Trusted Signing (Azure, ~$10/mo, faster approval)
- Until signed: Windows SmartScreen will warn on first run for most users

### Dashboard — two-column live device view ✅ (implemented)
System / Game columns wired to `_liveDevices` + `App.HidHide.GetBlacklist()` +
`SessionBlacklistIds`. Right column only populated while a session is active.

### Features
- UAC-free launch via Scheduled Task (no prompt when triggering from Steam/shortcut)
- Community profile presets (game-specific JSON contributions via PR)

### Architecture note (pnputil removed 2026-05-19)
pnputil backend, StateStore, DeviceController, and VidResolver were removed. The app is
now HidHide-only.

HandleWatcher question — **resolved**: removing it (see Recent decisions at top of file).
EAC kills the process for the DuplicateHandle pattern, and a timer is functionally
sufficient. The "wait for acquisition" phase is being replaced with a fixed
`InitialDelaySeconds` wait.

Shortcut export question — **deferred** for now. Process watcher + Dashboard launch
covers the common path; shortcuts are convenience for power users. No removal planned.
Steam wrapper kept for playtime tracking regardless.

### Devices tab — purpose clarification and UX cleanup ✅ (mostly done)
Banner + "Hiding active" toggle removal + VID:PID-as-tooltip + instance ID via context
menu all implemented.

**Still open:**
- **Device scope verification**: each Devices-tab row may map to multiple HID interface
  instance paths (composite HID like MI_00 + MI_01). When toggling such a row OFF,
  ensure all child interface paths are added to the persistent blacklist, not just the
  parent. Otherwise the device is only partially hidden.

### Games tab — Handle Watcher removal ✅
Decided 2026-05-19 (see Recent decisions): HandleWatcher is being removed entirely due
to EAC incompatibility. WaitForAcquisition replaced by `InitialDelaySeconds` timer.

### Games tab — reveal timing ✅ (resolved)
See Recent decisions at top: timer-based reveal with profile-level `InitialDelaySeconds`
(default 5s) + per-device `DelaySeconds` for spacing. HandleWatcher gone. The FH6 + MOZA
hot-plug analysis still holds — WGI re-enumerates on file-open success, so timer-based
reveal works as long as the game is hot-plug aware (documented limit).

### Profile device list — redesign for HidHide / unified UI ✅ (implemented)
Single ordered list with per-row role selector (AlwaysVisible / RevealAfterStart /
AlwaysHidden) + per-device delay + Up/Down/Remove buttons. Trigger mode removed in
favor of `InitialDelaySeconds` + per-device `DelaySeconds`. UI labels updated to new
terminology; underlying JSON keys (`keepEnabled`/`disableThenRestore`/`keepDisabled`)
kept stable for backwards compatibility.

### Idle / standby device profile (open)
A default profile that's always active when no game is running. Devices listed in it
stay disabled at all times unless a game profile takes over, then restores them to the
idle state (not necessarily all-enabled) when the game exits. Use case: keep the entire
sim rig invisible to Windows/other apps by default, only surface devices when a sim
game runs. Open questions:
- Modeled as a profile, or as a separate concept layered on top of the persistent BL?
- Does game-profile exit restore to "idle state" or to "all-enabled"? Currently the
  latter — needs unifying with idle if that ships
- Activates on app start, or only after first game session ends?

### Direction decision (blocks rename, README rewrite, icon)
- Is this a **gaming tool** (sim rig manager, FFB fix, tight sim-racing focus) or a
  **system tool** (general HID/controller manager, broader audience, PnP automation)?
- Currently the architecture is general but every feature and all docs are sim-racing-first
- Gaming tool: narrower audience, more passionate, README leads with "FFB every time",
  community presets, Steam integration front and center
- System tool: broader appeal, idle profile and input monitor make more sense,
  name leans more technical
- The idle profile feature is the clearest signal — it's a system tool feature, not a
  sim-racing-specific one. If that's in scope, lean system tool.
- This decision should be made before the rename, README rewrite, and icon work
  since all three depend heavily on which direction is chosen

### Project rename consideration
- "HID Reorder" is technically accurate (HID is the USB device class everything falls
  under — keyboards, mice, wheels, pedals, shifters, all of it) but it reads as jargon
  and doesn't communicate what the tool actually does to a normal user
- The app filters to game-controller-class HIDs specifically — keyboards and mice are
  excluded and there's essentially no reason to disable them for game compatibility.
  No shipping PC game assumes there's no keyboard attached.
- "Reorder" is also a bit misleading — the app doesn't reorder a list, it manipulates
  PnP enumeration timing to influence which device Windows assigns as controller slot #1
- Better name directions to consider:
  - Something around controller/input priority or sequencing
  - Something around sim rig management (narrower audience but honest)
  - Something that implies "hide devices from games"
- Rename is a meaningful effort: repo, binary name, mutex, IPC pipe name, AppData
  folder, registry/task scheduler entry, all XAML namespaces, README, releas3
- thinking "Controller Manager" as a play on device manager, because that is ultimately the full intent, not just for simracing devices 

### Device input monitor ✅ (implemented — needs testing)
- Live axis/button expander at the bottom of the Devices tab, polls only while open
- Uses raw HID via `HidD_GetPreparsedData` / `HidP_GetValueCaps` / `ReadFile`
- Also used to filter devices with zero HID input caps (Lian Li fans, audio devices)
  from the default view (still visible via Show All HID)
- **TODO: test with Bluetooth controllers** — Bluetooth HID devices have different
  instance ID formats and device paths than USB. The `ToDevicePath()` derivation
  (`HID#VID...#{guid}`) may or may not resolve correctly for BT devices. Specifically:
  - BT instance IDs often start with `BTHENUM\...` or `BTHLEDevice\...`, not `HID\`
  - The derived path might fail to open; verify `HidInputMonitor.Open()` succeeds
  - If it fails, may need to enumerate via SetupDi by device interface GUID rather
    than deriving the path from the instance ID
  - Also verify dedup logic works for BT — Bluetooth HID devices have no USB composite
    root, so `GetDedupeKey()` will fall back to instance ID normalization
- Display: progress bars for axes, colored squares for buttons; center-zero drift
  visualization and X/Y scatter plot are still backlog
- need to add: joystick visuals, controller triggers aren't showing up right

### HidHide integration ✅ (implemented, shipping)
HidHide is the only device-hiding backend now. pnputil and the MOZA exit-3010 workarounds
have been removed entirely. See `app/Services/HidHideClient.cs`.

**Reference (kept for future work):**
- Control device: `\\.\HidHide`, open with `GENERIC_READ`
- IOCTL formula: `CTL_CODE(32769, f, METHOD_BUFFERED, FILE_READ_DATA)` = `0x80014000 | (f << 2)`
- `IOCTL_GET/SET_WHITELIST`  (0x80016000/04) — persistent, **NT device paths**, multi-string
- `IOCTL_GET/SET_BLACKLIST`  (0x80016008/0C) — persistent, device instance paths, multi-string
- `IOCTL_GET/SET_ACTIVE`     (0x80016010/14) — BOOLEAN
- `IOCTL_GET/SET_WLINVERSE`  (0x80016018/1C) — BOOLEAN; true = whitelist acts as deny-list
- `IOCTL_ADD/CLR_SESSION_BLACKLIST` (0x80016020/24) — **only in modified driver** (our
  repo). Stock 1.4.181.0 silently ignores these. See "HidHide driver version
  compatibility" in Recent decisions for the persistent-BL workaround.
- C wrapper reference: `HidHide/HidHideCLI/src/FilterDriverProxy.cpp`

---

## Known limitations

- **Reveal phase requires hot-plug-aware games** — WGI, RawInput, or modern XInput.
  Pure legacy DirectInput-only games that do a one-shot startup enumeration can keep
  FFB (via pre-launch hiding) OR get late-revealed devices, not both. HidHide doesn't
  emit `DBT_DEVICEARRIVAL` on session BL changes, so non-listening games won't notice.
- **HidHide 1.4.181.0 has no session-blacklist IOCTLs** — we work around by using the
  persistent BL with snapshot/restore. CM crash mid-session leaves session devices in
  the persistent BL until manually re-enabled.
- **Anti-cheated games** (EAC, BattlEye) — HandleWatcher removed because it tripped
  EAC's cheat detection. Timer-based reveal works for any game; precision dropped
  slightly in exchange for safety.
- **Process watcher has a race window** — prefer Steam wrapper or shortcut for
  timing-sensitive games.
- **Shortcut icon extraction works for non-UWP games only** (direct .exe path).
- **Steam command requires UAC prompt on each launch** unless Scheduled Task workaround
  is used (see Backlog).
- **App is unsigned** — Windows SmartScreen will warn on first run until code signing
  is set up.

---

## Build notes

- Build: `dotnet.exe build` from `/app` (Windows binary, not WSL `dotnet`)
- Self-contained publish: `dotnet.exe publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish-sc`
- Slim publish: `dotnet.exe publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -o publish-fd`
- Release: push a `vX.Y.Z` tag to trigger GitHub Actions (builds both)
