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
8. ✅ Dashboard tab (profile picker, device summary lists, Launch/Abort/Restore All, activity log)
9. ✅ Steam wrapper (--steam-wrap CLI, disable→spawn game→wait→restore)
10. ✅ Process watcher (500ms poll, auto-triggers on game launch)
11. ✅ Shortcut export (WScript.Shell .lnk, Desktop + Start Menu buttons in Games tab)
12. ✅ Single-instance + named pipe IPC (mutex, \\.\pipe\HIDReorder, --launch forwarding)
13. ✅ Settings tab (Start with Windows, process watcher toggle, default trigger mode)

---

## v2 stretch goals

- System tray icon + per-profile quick-launch from tray
- Hotkey fallback trigger (user presses key when they feel FFB kick in)
- Timer fallback trigger (simpler than HandleWatcher for edge cases)
- UAC-free shortcuts via Scheduled Task ("Run with highest privileges")
- Profile presets — community JSON contributions for common games
- HidHide CLI backend option
- Per-device delay-before-enable override
- Import / export profiles
- Code signing (free option — SignPath.io for open source)
- Minimize to tray

---

## Known limitations

- HandleWatcher uses `NtQueryInformationProcess` (class 51) which requires `PROCESS_QUERY_INFORMATION` — works as admin
- Steam wrapper keeps a process alive for playtime tracking; if HIDReorder crashes mid-wrap, devices may stay disabled until next launch (state.json recovery handles this)
- Process watcher has a race window — prefer Steam wrapper or shortcut for timing-sensitive games
- Shortcut icon extraction works for non-UWP games only (direct .exe path)

---

## Build notes

- Build from `/app` directory: `dotnet.exe build` (Windows binary, not WSL `dotnet`)
- Publish: `dotnet.exe publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
- Release: push a `vX.Y.Z` tag to trigger GitHub Actions
