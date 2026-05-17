# TODO — HID Reorder

Reference this at the start of any session continuing work on this project.

---

## What this tool is

A per-game HID device profile manager for sim racing and controller games. The user defines profiles that describe which devices to disable before a game launches, and when to re-enable them. Think HidHide but per-game, automatic, and with a smart re-enable signal instead of a global toggle.

Three device roles per profile:
- **Keep Enabled** — never touch (wheel base)
- **Disable → Re-enable** — disable before launch, re-enable one-by-one as the game opens HID handles
- **Keep Disabled** — disable for the whole session, re-enable only on game exit (sim rig for controller games)

Unassigned devices are not touched.

---

## Why it works (root cause, confirmed)

FH6/FH5 assigns FFB to whichever `Windows.Gaming.Input` RawGameController it enumerates first at startup. When handbrake, pedals, and shifter are connected, they get picked before the wheel. The fix: disable them before launch, let the wheel be the only visible device, wait for the game to acquire it, then re-enable the rest.

MOZA with "Forza Compatibility Mode" presents as Fanatec (VID 0x0EB7), detected via `Fanatec.Devices.dll`. Same enumeration-order problem affects iRacing, older Codemasters titles, Dakar Rally, and any game that gets confused by multiple controllers.

**FH6 API notes:**
- Uses `Windows.Gaming.Input` — NOT DirectInput, NOT WinMM
- joy.cpl slot order is irrelevant to FH6
- `gameinputsvc.exe` holds open HID handles on behalf of FH5/FH6

---

## Launch paths (primary → fallback)

1. **Steam wrapper** — user adds `"HIDReorder.exe" --steam-wrap <profileId> -- %command%` to Steam Launch Options
2. **Generated shortcut** — `.lnk` pointing to `HIDReorder.exe --launch <profileId>`
3. **In-app Launch button** — same flow, from the Dashboard tab
4. **Process watcher** — background safety net, detects game launches not triggered by paths 1–3

---

## Project structure

- `/app` — new WPF app (active development)
- `/gui` — original WinForms prototype (keep runnable, do not extend)
- `/vid-names.json` — shared VID/PID name map, used by both apps

---

## MVP build status

### ✅ 1. WPF project scaffold
`app/HIDReorder.csproj` — .NET 10, WPF, `requireAdministrator`, MVVM, System.IO global using.

### ✅ 2. Device enable/disable
Using confirmed-working PowerShell `Enable-PnpDevice` / `Disable-PnpDevice` via `Services/DeviceController.cs`. `Native/SetupApi.cs` skeleton exists for future SetupDi work (DeviceInterfacePath enumeration).

### ✅ 3. Device enumeration + Devices tab
`Services/DeviceEnumerator.cs` — WMI HID class GUID, VID/PID extraction, BusReportedName via CfgMgr32, hub name suppression, `vid-names.json` enrichment. "Show all HID" toggle. Per-row ON/OFF toggle button. Click instance ID to copy.

### ✅ 4. Failsafe state.json
`Services/StateStore.cs` — writes before each disable, clears after each enable, auto-recovers on startup, hooks `OnExit`. `%LOCALAPPDATA%\HIDReorder\state.json`.

### ✅ 5. Profile model + persistence
`Models/Profile.cs` — three device lists (KeepEnabled, DisableThenRestore, KeepDisabled), TriggerMode, timer/hotkey config, game exe path. `Services/ProfileStore.cs` — JSON CRUD to `%LOCALAPPDATA%\HIDReorder\profiles.json`. Games tab with profile list, editor, three-list device assignment with unassigned-devices picker (assigned devices pruned from picker to prevent double-assignment).

---

## MVP — remaining

### 6. [ ] HandleWatcher  ← next up
`Services/HandleWatcher.cs` — polls target process handle table every 100ms via `NtQueryInformationProcess(ProcessHandleInformation=51)` + `DuplicateHandle` + `NtQueryObject`. Filter to `\Device\HID*` handle names. Emit `HidHandleOpened` for new handles (diff snapshot). Wrap each `NtQueryObject` in 100ms timeout.

Target PID selection:
- If `gameinputsvc.exe` is running → watch that (FH5/FH6)
- Else → watch game PID (iRacing, ACC, etc.)

Semantics:
- `WaitingForAcquisition`: first handle open = game has a device, advance to re-enable phase
- `RestoringDevices`: each handle open = re-enable next DisableThenRestore device; step timeout (default 1500ms) advances anyway

### 7. [ ] LaunchOrchestrator state machine
`Services/LaunchOrchestrator.cs`

States:
```
Idle → DisablingDevices → LaunchingGame → WaitingForAcquisition → RestoringDevices → Monitoring → Idle
```

- **DisablingDevices**: write state file, disable DisableThenRestore + KeepDisabled via DeviceController, abort+restore on failure
- **LaunchingGame**: `Process.Start(profile.GameExecutablePath)`; poll for `GameExecutableName` up to 30s
- **WaitingForAcquisition**: subscribe HandleWatcher (or timer/hotkey fallback), advance on first HID handle open
- **RestoringDevices**: for each device in DisableThenRestore order — enable, wait for HidHandleOpened (or timeout), clear from state file
- **Monitoring**: watch game process for exit; on exit re-enable KeepDisabled
- Hotkey fallback: user presses key to signal acquisition; re-enables use fixed `InterDeviceRestoreDelayMs`

### 8. [ ] Dashboard tab
- Active profile dropdown
- Three read-only device lists (summary view)
- **Launch** button
- State machine status line + per-device status during flow
- **Restore All** button (prominent when state file non-empty)
- Activity log — last ~20 events

### 9. [ ] Steam wrapper
`Cli/SteamWrapInvocation.cs` — `--steam-wrap <profileId> -- <game args>`. Disables devices, spawns game, keeps wrapper alive for Steam playtime tracking, re-enables on game exit. Games tab: **"Copy Steam launch command"** button.

### 10. [ ] Process watcher
`Services/ProcessWatcher.cs` — `Process.GetProcesses()` every 500ms, match `GameExecutableName`, skip if orchestrator already handling. Triggers same flow as Launch button.

### 11. [ ] Shortcut export
`.lnk` via `IWshShell` COM interop, target `HIDReorder.exe --launch <profileId>`. Icon from game exe. Games tab: "Create Desktop Shortcut" / "Add to Start Menu" buttons.

### 12. [ ] Single-instance + named pipe IPC
Mutex + `\\.\pipe\HIDReorder`. CLI args forwarded to running instance. Request types: `launch`, `steam-wrap-begin`, `steam-wrap-end`, `restore-all`, `status`.

### 13. [ ] Settings tab
Start with Windows, minimize to tray, watcher poll intervals, default trigger mode, "Show all HID" toggle.

---

## v2 stretch goals

- System tray icon + per-profile quick-launch from tray
- Timer fallback trigger (simpler than HandleWatcher for edge cases)
- UAC-free shortcuts via Scheduled Task
- Profile presets — community JSON contributions
- HidHide CLI backend option
- Per-device delay-before-enable override
- Import / export profiles
- Code signing (free option — SignPath.io for open source)

---

## Build notes

- Always build from the project directory: `dotnet.exe build` (Windows binary, not WSL `dotnet`)
- WinForms app: build from `/gui`
- WPF app: build from `/app`
- Publish: `dotnet.exe publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
