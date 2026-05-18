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
- HidHide CLI backend option

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
