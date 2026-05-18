# TODO — HID Reorder

Reference this at the start of any session continuing work on this project.

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

1. **Steam wrapper** — `"HIDReorder.exe" --steam-wrap <profileId> -- %command%` in Launch Options
2. **Shortcut** — `.lnk` pointing to `HIDReorder.exe --launch <profileId>`
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
12. ✅ Single-instance + named pipe IPC (mutex, \\.\pipe\HIDReorder, --launch forwarding)
13. ✅ Settings tab (Start with Windows, process watcher toggle, logging level, pin to top)
14. ✅ System tray icon + per-profile quick-launch from tray
15. ✅ File logging with Off/Normal/Verbose levels

---

## Backlog

### Games tab — device picker filtering
- The device picker in the Games tab (when assigning devices to a profile's Keep Enabled /
  Disable→Restore / Keep Disabled lists) should have the same "Show All HID" toggle as the
  Devices tab — currently it shows all enumerated devices with no way to filter down to just
  game controllers. Add a checkbox or toggle to the picker so users aren't wading through
  keyboards, fan controllers, and audio devices when building a profile.

### UAC / Steam integration
- Steam command triggers a UAC prompt on every launch because HIDReorder.exe has
  `requireAdministrator` in its manifest. If HIDReorder is already running in the tray,
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
`"C:\path\to\HIDReorder.exe" --launch <profileId>` in the cmd (blocking) field so
HIDReorder disables physical controllers before the game launches. On app exit, Sunshine
has a "Detach Command" field — put the same `--launch` there or rely on process watcher
to re-enable on game exit. Works the same way for Apollo (same web UI structure as a
Sunshine fork).

### Icon
- Current icon is placeholder. Need a real icon — suggest something with a
  joystick/controller and a reorder/sort visual. Can use Figma or commission.
  Replace `app/app.ico` (must be .ico format, ideally multi-size: 16/32/48/256px).

### Licensing
- Add MIT `LICENSE` file to repo root

### Code signing
- Apply to SignPath.io (free for OSS) — legitimate Authenticode signature, integrates with
  GitHub Actions. Takes a few days to approve. See signpath.io/product/open-source
- Alternative: Microsoft Trusted Signing (Azure, ~$10/mo, faster approval)
- Until signed: Windows SmartScreen will warn on first run for most users

### Export / import profiles
- Export: serialize selected profile (or all profiles) to a JSON file via Save dialog
- Import: load a JSON file, merge or replace existing profiles
- Useful for backup and sharing community profiles
- Single profile export should produce a standalone JSON anyone can drop in
- "Import all" could be a zip of multiple profile JSONs

### Features
- UAC-free launch via Scheduled Task (no prompt when triggering from Steam/shortcut)
- Per-device delay-before-enable override (some devices need settle time)
- Community profile presets (game-specific JSON contributions via PR)
- Alternative backend options for device hiding
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
  folder, registry/task scheduler entry, all XAML namespaces, README, releases

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

### Companion software handle conflict
- When a device's companion app (MOZA Pit House, Razer Synapse, Logitech GHub, etc.) holds
  an open handle to the HID device, Windows returns ERROR_NOT_SUPPORTED (exit 50) and refuses
  to disable it. This is not a driver restriction — it's Windows enforcing that you can't
  disable a device with active handles.
- Fix options:
  1. **Per-device process kill list** — profile or device setting listing process names to
     kill before disabling (e.g. "MozaPitHouse.exe"). Restart them after re-enabling.
  2. **Auto-detect by handle** — use NtQuerySystemInformation or handle enumeration to find
     which processes have handles to the device's HID path, then offer to close them.
  3. **Per-device service stop** — same as above but for Windows services rather than processes.
- Option 1 is the most practical to implement. The profile editor would have a "stop before
  launch / restart on exit" process list, similar to how some launchers handle anti-cheat.
- This applies generically to any device whose software keeps a handle open — not just MOZA.

### Code quality
- Code scan / review pass — check for dead code, obvious issues, security concerns
- Consider replacing PowerShell Enable/Disable-PnpDevice with direct SetupAPI calls
  to eliminate the ~1s per-device PowerShell startup overhead

---

## Known limitations

- HandleWatcher uses `NtQueryInformationProcess` (class 51) which requires `PROCESS_QUERY_INFORMATION` — works as admin
- Steam wrapper keeps a process alive for playtime tracking; if HIDReorder crashes mid-wrap, devices may stay disabled until next launch (state.json recovery handles this)
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
