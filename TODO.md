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

### Features
- UAC-free launch via Scheduled Task (no prompt when triggering from Steam/shortcut)
- Per-device delay-before-enable override (some devices need settle time)
- Community profile presets (game-specific JSON contributions via PR)
- Alternative backend options for device hiding

### Architecture: two-mode complexity — where does this go?
With HidHide as the primary backend and pnputil becoming a deprecated fallback, several
features built specifically for the pnputil model may be unnecessary in HidHide mode, and
the growing `if (UseHidHide)` branches are a signal worth paying attention to.

**HandleWatcher** — may be obsolete for HidHide.
pnputil needed it because: you PnP-disable devices before launch, the game starts and
scans DirectInput/WGI, you watch for the game to open a handle to the first device (the
wheel), THEN re-enable the rest in sequence. The handle-open event was the only signal
that the game had "acquired" the first device.
With HidHide, hiding is at the file-open level. The game starts, tries to open hidden
devices, gets ACCESS_DENIED, and continues — all in milliseconds. You can update the
session blacklist at any point to reveal more devices. You don't need to watch handles to
know when to reveal; a simple timer or even just "wait N ms after game starts" might be
sufficient, since HidHide's reveal is instantaneous and the game will retry device
enumeration on its own if the first scan misses something. Open question: does FH6
re-enumerate after startup, or is the DirectInput burst truly one-shot? If one-shot, the
ordering still matters and we need a timing signal — just not necessarily handle-watching.
If games re-enumerate periodically, a timer is sufficient.

**Shortcut export (Desktop / Start Menu / Steam links)** — may be less necessary.
These exist because with pnputil you MUST pre-disable devices before the game process
starts — there's a narrow window at game launch where DirectInput scans all devices, and
if they're not already disabled, the wrong device gets slot #1. The shortcuts/steam-wrap
ensure disable happens before the game exe runs.
With HidHide the driver hooks `PsSetLoadImageNotifyRoutine` — access-denial is in effect
from the moment the process image loads, before any userspace code runs. So even launching
the game directly (without our wrapper) and THEN calling `BeginGameSession` a split-second
later might work. That changes the value proposition of the launch shortcuts: they're no
longer strictly required, just convenient for triggering the profile. Process watcher may
be sufficient for most workflows. Steam wrapper (--steam-wrap) keeps the process alive for
playtime tracking, which is independent of device hiding — still useful but for a different
reason.

**What this means for the codebase:**
The current structure has two fully parallel code paths with conditionals throughout
LaunchOrchestrator, DevicesViewModel, etc. There are a few directions:
1. **Accept the two-path model** — it's temporary; pnputil path is legacy code on its way
   out. Keep it, don't invest in it further, remove it once HidHide adoption is confirmed.
2. **Make HidHide the only supported path** and remove pnputil entirely. Simpler code,
   but requires HidHide as a hard prerequisite.
3. **Redesign around HidHide's model** as the primary abstraction, with pnputil as a thin
   compatibility shim that maps the new API onto the old behaviour where possible.

Leaning toward option 1 for now: the pnputil path is already working and tested, leave it
alone, let the HidHide path be the one that gets new features (the profile redesign, the
cleaner Devices tab). When HidHide has been stable for a release cycle, remove pnputil.
The conditionals are a smell but they're contained — they don't need to be fixed urgently.

**Concrete things to decide before acting:**
- Does FH6 re-enumerate DirectInput devices after startup or is it truly one-shot? (Test:
  with HidHide, launch FH6 normally, then quickly run BeginGameSession — does the wheel
  get FFB?)
- If FH6 is one-shot, HandleWatcher is still needed as a timing signal even with HidHide.
  If not, the whole "wait for acquisition" phase can be replaced with a fixed delay or
  removed entirely for HidHide sessions.
- Is there a real use case for Desktop/Start Menu shortcuts once process watcher + HidHide
  works reliably? If process watcher covers auto-trigger and dashboard covers manual launch,
  shortcuts may just be "nice to have" for power users.

### Devices tab cleanup for HidHide mode
When HidHide is the active backend, the Devices tab shows information that belongs to
the pnputil world and is confusing or meaningless in a HidHide context:
- **VID:PID column**: internal plumbing — the user doesn't need it here (it's for profile
  building and debugging, not day-to-day use). Hide when HidHide is the backend, or move
  it to a tooltip / "Show details" toggle.
- **Instance ID column**: even more internal. Definitely hide by default in HidHide mode.
- **Device names**: the current name comes from WMI `Win32_PnPEntity.Name`, which may differ
  from what HidHide's own UI shows (which uses the `BusReportedDeviceDesc` property and/or
  HID top-level collection names). Investigate whether HidHide's naming produces cleaner
  results and align with it when HidHide is active, so the user sees the same device name
  across both tools.
- **Device scope**: HidHide operates at the HID interface level. The current Devices tab
  deduplicates by USB composite root (collapses MI_00 + MI_01 of the same physical device
  into one row) while HidHide targets individual instance paths. Verify that what we show
  in the Devices tab corresponds 1:1 to what we put in the HidHide blacklist — a device
  that appears as one row in our UI should map to exactly the instance path(s) HidHide uses.
  If there's a mismatch, the device may not actually be hidden despite the toggle showing OFF.

### Profile device list — redesign for HidHide / unified UI
The current three-list model (Keep Enabled / Disable→Re-enable / Keep Disabled) was
designed around pnputil's mental model of individually disabling/re-enabling devices via
PnP. With HidHide as the primary backend, the framing shifts: devices aren't "disabled",
they're "hidden from the game". The ordering of devices matters (which one the game sees
first determines FFB assignment), and the three separate list boxes make that hard to see.

Proposed redesign — a single ordered device list with per-row controls:
- One list showing all devices in the profile, in order from top to bottom
- Up ↑ / Down ↓ buttons (or drag handles) to set the reveal order
- Each row has a role selector, replacing the three lists:
  - **Always visible** — game can see this device the whole time (was "Keep Enabled")
  - **Reveal after start** — hidden at launch, revealed once game acquires the first
    "always visible" device (was "Disable→Re-enable" / HandleWatcher phase)
  - **Always hidden** — never visible to this game (was "Keep Disabled")
- The ordering within "Reveal after start" rows determines the sequential reveal sequence
  (currently this is the order of the DisableThenRestore list)
- The visual hierarchy makes it clear: top of the list = what the game sees first
- Consider a summary line: "Game sees 1 device at launch, then 3 more in sequence, 2 always hidden"

Naming audit — terms to replace across the codebase and UI once this ships:
- "Keep Enabled" → "Always Visible" (or "Always On")
- "Disable→Re-enable" / "Disable Then Restore" → "Reveal After Start" (or "Sequential")
- "Keep Disabled" → "Always Hidden"
- "Disabling devices" (Dashboard status) → "Hiding devices"
- "Re-enabling" → "Revealing" or "Restoring"
The JSON profile keys (`keepEnabled`, `disableThenRestore`, `keepDisabled`) should stay
unchanged for backwards compatibility — rename only in UI strings and code symbols.
- **Idle/standby device profile** — a default profile that's always active when no game
  is running. Devices listed in it stay disabled at all times unless a game profile takes
  over, then restores them to the idle state (not necessarily all-enabled) when the game
  exits. Use case: keep the entire sim rig invisible to Windows/other apps by default,
  only surface devices when a sim game runs — no kernel driver or service needed,
  just PnP toggling. Need to think through:
  - What "idle profile" means for the three device roles (probably just a Keep Disabled list)
  - How game profile exit interacts with idle state: currently "exit" means re-enable
    everything, but with an idle profile it should restore to idle state instead (i.e.
    re-disable the sim rig). These two restore paths need to be unified or the idle
    profile will be silently overridden every time a game exits
  - Whether idle profile activates on app start, or only after first game session ends

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

### MOZA driver / pnputil disable behaviour (investigated 2026-05-18)
The real failure mode for the MOZA wheel is NOT a companion software handle conflict —
it is the MOZA Windows Driver returning **exit 3010** (ERROR_SUCCESS_REBOOT_REQUIRED) on
`pnputil /disable-device`. Tested with Pit House + SimHub both open; made no difference.

What exit 3010 actually means:
- The driver cannot fully stop its stack (it holds internal state / open handles)
- BUT: `ConfigFlags=1` is written to the registry immediately, and the HID interface
  disappears from DirectInput right away — the device IS invisible to games
- The driver fully unloads on next reboot

Current workaround in `DeviceController.cs`:
- **Disable**: exit 3010 and exit 50 "pending system reboot" with ConfigFlags=1 both
  treated as success — device is already invisible
- **Enable**: exit 50 "pending system reboot" falls back to clearing ConfigFlags=0 in
  the registry + `pnputil /scan-devices` to re-enumerate without a reboot
- **IsEnabled in DeviceEnumerator**: checks ConfigFlags from registry in addition to WMI
  ConfigManagerErrorCode=22, so the Devices tab toggle reflects reality after a 3010

Known remaining limitation with pnputil backend:
- After a ClearConfigFlags+scan restore, the MOZA driver's internal pending state
  survives. A second disable in the same session hits exit 50 with ConfigFlags=0 —
  pnputil cannot disable the device again until a full reboot. The Devices tab will
  show an error "The driver did not release its pending state after recovery. Reboot
  Windows to restore normal operation." This is a fundamental MOZA driver limitation,
  not fixable in userspace. **The HidHide backend (see below) eliminates this entirely.**

### HidHide integration — preferred device-hiding backend ⬅ START HERE next session
**Why**: pnputil disable/enable is a system-wide PnP operation. The MOZA Windows Driver
makes it unreliable (exit 3010, pending state, reboot required between cycles). The right
fix is a kernel filter driver that intercepts at the file-open level instead.

**Decision: use HidHide as-is, no driver modification needed.**
- HidHide (github.com/nefarius/HidHide) is an MIT-licensed, pre-signed WDF filter driver
- It sits above the HID class driver and returns STATUS_ACCESS_DENIED on device file opens
  for processes that shouldn't see a device — no PnP disable, no pending state, no reboot
- All allow/deny list changes are **fully runtime** — verified from source (Logic.c:863-931):
  `SetWhitelist()` and `SetBlacklist()` update the in-memory collection under a lock and
  flush the evaluation cache immediately, no driver restart required
- Session blacklist (`IOCTL_ADD_SESSION_BLACKLIST`, Logic.c:591) is purely in-memory and
  auto-cleans when our process exits — cleaner than state.json for failsafe recovery

**How hiding works** (Logic.c:155-248 `OnDeviceFileCreate`):
- When any process tries to open a HID device file, the driver checks:
  1. Is the device blacklisted?
  2. Is the calling process on the whitelist?
- If blacklisted and not whitelisted → STATUS_ACCESS_DENIED
- The device still appears in Device Manager and GetRawInputDeviceList — it's not
  removed from enumeration, just inaccessible. DirectInput skips devices it can't open.

**Startup timing**: works for Forza's ~30ms DirectInput burst. Rules are set before
game launch. Game process starts, loads image, tries to open HID device → ACCESS_DENIED
from the very first attempt. Verified: driver hooks PsSetLoadImageNotifyRoutine so the
check is in place from the moment the process image loads (Config.c).

**Per-game profile workflow** (no driver modification required):
1. HidHide installed as a prerequisite (MSI from nefarius/HidHide releases)
2. User adds companion apps (Pit House, SimHub) to HidHide whitelist once — they always
   keep access regardless of what's in the blacklist
3. Before game launch → `IOCTL_ADD_SESSION_BLACKLIST` with profile's device instance paths
4. Launch game → game gets ACCESS_DENIED on those devices, never sees them
5. Game exits → `IOCTL_CLR_SESSION_BLACKLIST` or auto-cleans when our process exits
6. Different profile next session → different devices in session blacklist

**IOCTL interface** (Shared/HidHideIoctlContract.h):
- Control device path: `\\.\HidHide`
- Open with `GENERIC_READ` (all IOCTLs use `FILE_READ_DATA` access)
- `IOCTL_GET/SET_WHITELIST`  (0x80016000/04) — persistent, **NT device paths** (`\Device\HarddiskVolumeX\...`), multi-string
- `IOCTL_GET/SET_BLACKLIST`  (0x80016008/0C) — persistent, device instance paths, multi-string
- `IOCTL_GET/SET_ACTIVE`     (0x80016010/14) — BOOLEAN (1 byte) on/off
- `IOCTL_GET/SET_WLINVERSE`  (0x80016018/1C) — BOOLEAN; when true, whitelist is a deny-list (only listed processes are blocked)
- `IOCTL_ADD_SESSION_BLACKLIST` (0x80016020) — in-memory, keyed by caller PID, auto-cleans on our exit
- `IOCTL_CLR_SESSION_BLACKLIST` (0x80016024) — clears session entries for calling PID only
- All multi-string data as UTF-16 null-separated, double-null terminated (REG_MULTI_SZ)
- C++ wrapper reference: HidHideCLI/src/FilterDriverProxy.cpp
- IOCTL formula: CTL_CODE(32769, function, METHOD_BUFFERED=0, FILE_READ_DATA=1) = 0x80014000 | (function << 2)
- Note: GET operations use first call with null output buffer to query needed byte size

**What to build**:
1. `HidHideClient.cs` service — `DeviceIoControl` P/Invoke wrapper, multi-string
   serialisation, typed methods for each IOCTL. Detect driver presence by attempting
   to open `\\.\HidHide` — gracefully absent if HidHide not installed.
2. `IDeviceHider` interface — abstract over pnputil vs HidHide so both backends coexist.
   HidHide preferred when available, pnputil fallback for users who haven't installed it.
3. `LaunchOrchestrator` integration — replace `DeviceController.SetEnabled(false)` calls
   with `HidHideClient.AddSessionBlacklist(profileDevices)` before launch, and
   `HidHideClient.ClearSessionBlacklist()` on game exit. HandleWatcher re-enable
   sequencing is no longer needed for HidHide-managed devices.
4. Settings tab — "HidHide detected ✓ / Not installed (download)" status indicator.
   Link to HidHide MSI. Button to open whitelist manager (companion app configuration).
5. Whitelist manager UI — list of processes allowed to see all devices regardless of
   profile. Pre-populate with common companion apps (Pit House, SimHub, GHub, Synapse).

**What to remove when HidHide is the backend**:
- `ClearConfigFlagsAndScan()` in DeviceController.cs — the ConfigFlags+scan workaround
- `ReadConfigFlags()` in DeviceController.cs and DeviceEnumerator.cs
- The exit 3010 / exit 50 special-case handling in `RunPnpCommandWithFallback`
- ConfigFlags-based `isEnabled` override in DeviceEnumerator.cs
- Keep pnputil path as fallback for users without HidHide

**Driver modification considered and deferred**:
Adding per-target-process hiding (hide device only from Game A, not Game B) to the
session blacklist would require ~70 lines of C in the kernel driver (Logic.h:56,
Logic.c:794, 828, 658, 730). Feasible. Would open a PR to nefarius/HidHide — he
declined a similar feature request in Discussion #60 but that was about redesigning
the main whitelist; extending the session blacklist is incremental (follows PR #201).
Not needed for the core use case (one game at a time). Defer until unmodified HidHide
is working end-to-end.

### Code quality
- Code scan / review pass — check for dead code, obvious issues, security concerns
- Consider replacing PowerShell Enable/Disable-PnpDevice with direct SetupAPI calls
  to eliminate the ~1s per-device PowerShell startup overhead

---

## Known limitations

- HandleWatcher uses `NtQueryInformationProcess` (class 51) which requires `PROCESS_QUERY_INFORMATION` — works as admin
- Steam wrapper keeps a process alive for playtime tracking; if Controller Manager crashes mid-wrap, devices may stay disabled until next launch (state.json recovery handles this)
- Process watcher has a race window — prefer Steam wrapper or shortcut for timing-sensitive games
- Shortcut icon extraction works for non-UWP games only (direct .exe path)
- Steam command requires UAC prompt on each launch unless Scheduled Task workaround is used
- App is unsigned — Windows SmartScreen will warn on first run until code signing is set up

---

## Build notes

- Build: `dotnet.exe build` from `/app` (Windows binary, not WSL `dotnet`)
- Self-contained publish: `dotnet.exe publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish-sc`
- Slim publish: `dotnet.exe publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -o publish-fd`
- Release: push a `vX.Y.Z` tag to trigger GitHub Actions (builds both)
