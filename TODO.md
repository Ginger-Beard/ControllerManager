# TODO — HID Reorder

Reference this at the start of any session continuing work on this project.

---

## What this tool is

A per-game HID device profile manager for sim racing and controller games. The user defines profiles that describe which devices to disable before a game launches, and when to re-enable them. Think HidHide but per-game, automatic, and with a smart re-enable signal instead of a global toggle.

Two profile types:
- **Wheel profiles** — disable pedals/handbrake/etc. before launch so the wheel base gets first-enumerated. Re-enable devices one-by-one as the game opens HID handles for them (staggered, driven by HandleWatcher).
- **Controller-only profiles** — disable the entire sim rig while a casual/controller game runs. Re-enable everything on game exit. No handle-watching needed.

---

## Why it works (root cause, confirmed)

FH6/FH5 assigns FFB to whichever `Windows.Gaming.Input` RawGameController it enumerates first at startup. When handbrake, pedals, and shifter are connected, they get picked before the wheel. The fix: disable them before launch, let the wheel be the only visible device, wait for the game to acquire it, then re-enable the rest.

MOZA with "Forza Compatibility Mode" presents as Fanatec (VID 0x0EB7), detected via `Fanatec.Devices.dll`. Once it's the only device at launch, FH6 picks it correctly.

Same enumeration-order problem affects iRacing, older Codemasters titles, Dakar Rally, and any game that gets confused by multiple controllers.

**FH6 API notes (for reference):**
- Uses `Windows.Gaming.Input` — NOT DirectInput, NOT WinMM
- joy.cpl slot order is irrelevant to FH6
- `gameinputsvc.exe` holds open HID handles on behalf of FH5/FH6 — not the game process itself

---

## Launch paths (primary → fallback)

1. **Steam wrapper** (primary for Steam games) — user adds `"HIDReorder.exe" --steam-wrap <profileId> -- %command%` to Steam Launch Options. App disables devices, runs the real game command, monitors process, re-enables on exit. Steam tracks playtime correctly because the wrapper stays alive.
2. **Generated shortcut** (non-Steam games) — app creates a `.lnk` pointing to `HIDReorder.exe --launch <profileId>`. Double-click triggers the full flow.
3. **In-app Launch button** — same flow, triggered from the dashboard. Primary for testing.
4. **Process watcher** — background safety net. Detects game launches that didn't go through paths 1–3.

---

## Direction: rebuild as WPF app

The current WinForms prototype (`/gui`) proved the concept and is the working workaround. All new work goes into a new WPF project. Do not extend the WinForms app.

Reusable from current app: `vid-names.json`, `VidResolver`, WMI enumeration logic, VID/PID matching.

Stack: **.NET 10, WPF, MVVM**, SetupDi P/Invoke (not PowerShell), `requireAdministrator` manifest.

---

## MVP build order

### 1. [ ] New WPF project scaffold
- `HIDReorder/HIDReorder.csproj` — .NET 10, WPF, `requireAdministrator` manifest
- Project structure: `Views/`, `ViewModels/`, `Services/`, `Native/`, `Models/`, `Cli/`
- `App.xaml.cs`: parse CLI args, check singleton mutex, forward to IPC client if already running
- Copy `vid-names.json` + `VidResolver` from current app

### 2. [ ] SetupDi P/Invoke — replace PowerShell
- `Native/SetupApi.cs` — `SetupDiGetClassDevs`, `SetupDiEnumDeviceInfo`, `SetupDiSetClassInstallParams`, `SetupDiCallClassInstaller` with `DIF_PROPERTYCHANGE`
- `Services/DeviceController.cs` — enable/disable by InstanceId
- Must work before failsafe (item 4) can be built

### 3. [ ] Device enumeration service
- `Services/DeviceEnumerator.cs` — WMI `Win32_PnPEntity`, HID class GUID (lift from current `DeviceManager.cs`)
- Capture: friendly name, VID/PID, InstanceId, DeviceInterfacePath, enabled state
- "Show all HID" toggle; default filter to gamepad/joystick usage pages
- `DevicesViewModel.cs` + Devices tab — live list, click-to-copy InstanceId/interface path

### 4. [ ] Failsafe state.json  ← do not ship without this
- `Services/StateStore.cs` — `%LOCALAPPDATA%\HIDReorder\state.json`
- Write InstanceId to file **before** each disable call
- Remove from file **after** each enable call succeeds
- On app startup: read state file, re-enable anything in it, show recovery toast
- Hook `AppDomain.CurrentDomain.ProcessExit` + `UnhandledException` → `RestoreAll()`
- **Restore All** button always visible in Dashboard, highlighted when state file is non-empty

### 5. [ ] Profile model + persistence
- `Models/Profile.cs` — Id, Name, LaunchCommand, GameExecutableName, ProfileType (WheelProfile / ControllerProfile), KeepEnabled[], DisableThenRestore[] (ordered), TriggerMode, timing config
- `Models/DeviceRef.cs` — InstanceId, DeviceInterfacePath, FriendlyName
- `Services/ProfileStore.cs` — CRUD to `%LOCALAPPDATA%\HIDReorder\profiles.json`
- `GamesViewModel.cs` + Games tab — profile list + editor, two side-by-side device lists with drag between

### 6. [ ] HandleWatcher  ← prove out early, core component
- `Services/HandleWatcher.cs` — polls target process handle table every 100ms
- P/Invoke: `NtQueryInformationProcess(ProcessHandleInformation=51)`, `DuplicateHandle`, `NtQueryObject(ObjectNameInformation=1)`
- Filter to handle names matching `\Device\HID*`
- Emit `HidHandleOpened(deviceInterfacePath, processId)` for new handles (diff previous snapshot)
- Wrap each `NtQueryObject` in a 100ms timeout — some object types stall
- **Target PID**: if `gameinputsvc.exe` is running → watch that; else → watch game PID
- Only needs `PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION` — no debug rights needed

Semantics during flow:
- `WaitingForAcquisition`: first `HidHandleOpened` = game has a device, advance to re-enable phase
- `RestoringDevices`: each `HidHandleOpened` = game accepted the last device, re-enable next one; timeout `HandleWatcherStepTimeoutMs` (default 1500ms) advances anyway
- Controller-only profiles skip this phase entirely

### 7. [ ] LaunchOrchestrator state machine
- `Services/LaunchOrchestrator.cs`
- **Wheel profile** states: `Idle → DisablingDevices → LaunchingGame → WaitingForAcquisition → RestoringDevices → Monitoring → Idle`
- **Controller-only profile** states: `Idle → DisablingDevices → LaunchingGame → Monitoring → Idle` (no handle watching, re-enable on game exit)
- DisablingDevices: write state file, SetupDi disable, abort + restore on failure
- LaunchingGame: `Process.Start(profile.LaunchCommand)`; poll for `GameExecutableName` up to 30s
- WaitingForAcquisition: subscribe HandleWatcher, advance on first HID handle open
- RestoringDevices: for each device in order — enable, wait for HidHandleOpened (or timeout), remove from state file
- Monitoring: watch game process for exit; on exit RestoreAll remaining
- **Hotkey fallback**: `TriggerMode.Hotkey` — user presses key to manually signal acquisition; re-enables use fixed `InterDeviceRestoreDelayMs`

### 8. [ ] Steam wrapper
- `Cli/SteamWrapInvocation.cs` — handles `--steam-wrap <profileId> -- <game args>`
- Coordinates with main app via IPC: `steam-wrap-begin` (disable devices) → `ready` → spawn real game command → `steam-wrap-end` on game exit (re-enable)
- Wrapper process stays alive for Steam playtime tracking
- Games tab profile editor: **"Copy Steam launch command"** button — copies the full `%command%` string ready to paste into Steam Launch Options

### 9. [ ] Dashboard tab
- Active profile dropdown
- KeepEnabled / DisableThenRestore read-only device lists
- **Launch** button
- State machine status line + per-device status during flow
- **Restore All** button (prominent when state file non-empty)
- Activity log — last ~20 events

### 10. [ ] Process watcher
- `Services/ProcessWatcher.cs` — `Process.GetProcesses()` every 500ms
- Match enabled profiles by `GameExecutableName` (case-insensitive)
- Skip if orchestrator already handling that profile+process
- Triggers same flow as Launch button, skips `LaunchingGame` state

### 11. [ ] Shortcut export
- Games tab: **"Create Desktop Shortcut"**, **"Add to Start Menu"**
- `.lnk` via `IWshShell` COM interop, target `HIDReorder.exe --launch <profileId>`
- Icon: `ExtractAssociatedIcon` from game exe, cache as `.ico`

### 12. [ ] Single-instance + named pipe IPC
- Mutex: `Global\HIDReorder-{stable-guid}`
- Named pipe: `\\.\pipe\HIDReorder` — JSON request/response
- Request types: `launch`, `steam-wrap-begin`, `steam-wrap-end`, `restore-all`, `status`
- CLI args forwarded to running instance if mutex already held

### 13. [ ] Settings tab
- Start with Windows, start minimized to tray
- Process watcher poll interval (default 500ms)
- HandleWatcher poll interval (default 100ms)
- Default trigger mode for new profiles
- "Show all HID devices" toggle

---

## v2 stretch goals

- System tray icon + minimize-to-tray + per-profile quick-launch from tray menu
- Timer fallback trigger (simpler than HandleWatcher for users who can't get it working)
- UAC-free shortcuts via Scheduled Task ("Run with highest privileges")
- Profile presets — community-contributed JSON profiles for common games
- HidHide CLI backend option (use HidHide for hiding instead of disable/enable)
- Per-device "delay before enable" override
- Import / export profiles
- WMI `Win32_ProcessStartTrace` for lower-latency process detection

---

## Current WinForms app (`/gui`) — keep but don't extend

Working workaround while the new app is built. When the new app reaches parity on the disable → launch → re-enable flow, the old one can be retired.

Old TODO items absorbed into new app:
- Columnar device list → Devices tab
- Progress bar → Dashboard status line + activity log
- Enable/disable toggle → Devices tab

Old TODO items dropped (superseded):
- WinMM enumeration for slot numbers — joy.cpl order irrelevant to WGI-based games
- pnputil /remove-device approach — superseded by device isolation before launch
- Registry slot order manipulation — same
- FH6 WheelVidPid binary table investigation — not needed for the fix to work

---

## Build notes

- WinForms app: build from `/gui` with `dotnet.exe build` (Windows binary, not WSL `dotnet`)
- New WPF app: same, build from `HIDReorder/` directory
- Publish: `dotnet.exe publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
