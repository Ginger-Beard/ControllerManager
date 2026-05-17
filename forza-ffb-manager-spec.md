# Game FFB Device Order Manager — Build Spec (v4)

## Problem

Forza Horizon (5, 6, and likely future titles) assigns FFB output to the first gamepad-class HID device it enumerates at startup. Enumeration order is non-deterministic when multiple HID gamepads are connected (wheelbase, pedals, handbrake, shifter, button boxes, gamepads, joysticks), so the wheelbase frequently doesn't get FFB.

The game ships with an aggressive built-in anti-cheat that has handed out permanent bans for in-process tampering (DLL injection, hooks, debuggers). The fix must happen at the PnP / device layer, **never inside the game process**.

Manual workaround the tool replaces:
1. Disable every non-wheelbase HID device in Device Manager.
2. Launch the game.
3. Wait until the wheelbase has FFB (friction returns).
4. Re-enable the other devices one at a time.

The same pattern applies to any game with device enumeration order bugs (iRacing setups with many devices, Dakar Rally, older Codemasters titles, etc.), so the tool is designed around per-game profiles, not Forza-specifically.

## Goal

A Windows desktop app where the user:
1. Defines one or more **game profiles**, each with: launch command, executable name, devices to keep enabled, devices to disable-then-restore (in order), and timing config.
2. Triggers the disable → launch → wait-for-FFB-lock → restore flow via one of several entry points (see "Launch paths" below).
3. On game exit (or app crash, or BSOD recovery), every device the app disabled is guaranteed to be re-enabled.

## Tech stack

- **.NET 8**, **C#**
- **WPF** for UI (simpler than WinUI 3, full P/Invoke and WMI access, no packaging hassle)
- **System.Management** (WMI) for HID device enumeration
- **P/Invoke to SetupAPI** for enable/disable
- **P/Invoke to NTDLL** for handle table observation (`NtQueryInformationProcess`, `NtQueryObject`)
- **HidSharp** (NuGet) for optional HID FFB report sniffing fallback trigger
- **app.manifest** with `requireAdministrator` — PnP enable/disable + handle table queries require admin
- **System.Text.Json** for profile/state persistence
- **Named pipes** (`System.IO.Pipes`) for single-instance IPC
- **IWshShell** via COM interop for `.lnk` shortcut creation

## Launch paths

The app supports **four distinct trigger sources**, all converging on the same `LaunchOrchestrator` state machine. Listed in priority order (first three avoid the race window; the fourth has it):

1. **Manual Launch button** — in-app dashboard button. Disables devices first, then launches the game.
2. **Generated shortcut** — desktop or Start menu `.lnk` file produced by the app. Invokes `GameFFBManager.exe --launch <profileId>`. Same flow as Launch button.
3. **Steam launch wrapper** *(v2)* — user adds `"<path>\GameFFBManager.exe" --steam-wrap <profileId> -- %command%` to a game's Steam Launch Options. The wrapper coordinates with the main app to disable devices before invoking the real game command, then keeps the wrapper process alive for the game's lifetime so Steam tracks playtime correctly.
4. **Process watcher** — polls running processes every 500ms; if a configured game's exe appears without being triggered by paths 1–3, fires the flow reactively (with the documented race window).

The first three paths guarantee correct ordering. The fourth is the safety net.

## The core observer: HandleWatcher

The single most important runtime component. Watches the game's (or GameInput service's) open HID handles and emits events when new ones appear. Drives **both** the FFB-lock detection and the staggered re-enable timing — they're the same mechanism interpreted at different state-machine phases.

### Why this is the right primitive

- When the game opens its **first** HID handle, that's the FFB-lock signal — wheel is now bound. → Advance from `WaitingForFfbLock` to `RestoringDevices`.
- After each re-enable, the game receives the PnP arrival, enumerates, and opens a handle to the new device. That handle-open event is the cue to advance to the next re-enable. → Drives the `RestoringDevices` loop one step at a time.

One observer, two semantic interpretations. The fixed `InterDeviceRestoreDelayMs` and the hotkey/timer/HID-sniff triggers become *fallback options* rather than the primary mechanism.

### Implementation

- **Target PID selection at flow start**:
  - If `gameinputsvc.exe` is running → watch that PID. Modern Forza (FH5/FH6) routes HID via the GameInput service, so handles live there, not in the game process.
  - Else → watch the game's PID. Older DirectInput / RawInput games (iRacing, ACC, Codemasters titles) hold handles directly.
  - Optional: watch both and union events for maximum robustness.
- **Mechanism**: poll the target process's handle table every 100ms via `NtQueryInformationProcess` with class `ProcessHandleInformation` (value `51`). For each handle, get the kernel object's name via `NtQueryObject` with class `ObjectNameInformation`. Filter to names that start with `\Device\HID` or match a known HID device interface path.
  - Per-handle `NtQueryObject` calls can stall on certain object types. Wrap each call in a 100ms timeout via a worker thread + cancellation token. The Process Hacker / SystemInformer source code is the canonical reference for safe handle-table walking.
- **Event emission**: diff against the previous snapshot, emit `HidHandleOpened(deviceInterfacePath, processId, timestamp)` for new handles.
- **Subscription model**: `LaunchOrchestrator` subscribes only during `WaitingForFfbLock` and `RestoringDevices` phases. Idle otherwise (don't burn CPU watching when nothing's happening).

### Semantics during the flow

**WaitingForFfbLock**: emit the lock signal on the **first** `HidHandleOpened` event after `LaunchingGame` completed. Match on any HID device — don't try to verify it's the wheelbase specifically (it should be, given enumeration order, but verifying adds complexity and matching by interface path is brittle across reboots for some vendors).

**RestoringDevices**: after re-enabling device N, wait for **any** `HidHandleOpened` event by the target PID. Do NOT require the open to match the specific device we just re-enabled — Forza may open the newly-arrived devices in a different order than we re-enabled them, and waiting for a specific match would deadlock. As soon as *some* new HID handle opens, advance to re-enabling device N+1.

**Fallback timeout**: each wait has a per-step timeout (default 1500ms). On timeout, log a warning and advance anyway. Better to over-restore than to hang the flow.

### Fallback triggers (optional, configurable per profile)

For users on systems where handle-table observation misbehaves, or for games that don't open HID handles the way we expect:

- **Hotkey**: user presses configured key once FFB is felt. FFB-lock only — re-enables still use timing.
- **Timer**: wait N seconds after game's `MainWindowHandle != 0`.
- **HID-sniff** (HidSharp): observe FFB OUTPUT reports on the wheelbase. FFB-lock only.

Profile UI lets the user pick `HandleWatcher` (default) or one of the fallbacks. If a fallback is selected, `RestoringDevices` uses the fixed `InterDeviceRestoreDelayMs`.

## Core requirements

### 1. Device enumeration

- List devices in `GUID_DEVCLASS_HIDCLASS` = `{745a17a0-74d3-11d0-b6fe-00a0c90f57da}`.
- For each device, capture friendly name, manufacturer, VID/PID, **Device Instance ID** (primary key — stable across reboots), and **Device Interface Path** (e.g. `\\?\HID#VID_xxxx&PID_xxxx#...`) — needed by HandleWatcher to recognize opens, current enabled/disabled state (`ConfigManagerErrorCode` from WMI; `CM_PROB_DISABLED` = 22).
- Refresh button — devices come and go.
- Optional filter: HID Usage Page 0x01 (Generic Desktop), Usage 0x04 (Joystick) / 0x05 (Gamepad) / 0x08 (Multi-axis controller). Hides keyboards/mice from device pickers by default. "Show all HID" toggle exposes everything.

### 2. Profile model

```csharp
public class Profile {
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string LaunchCommand { get; set; }                 // full .exe path OR "shell:AppsFolder\<PFN>!App"
    public string GameExecutableName { get; set; }            // for process watcher matching, e.g. "ForzaHorizon5.exe"
    public bool ProcessWatcherEnabled { get; set; } = true;
    public List<DeviceRef> KeepEnabled { get; set; }
    public List<DeviceRef> DisableThenRestore { get; set; }   // ORDERED
    public TriggerMode FfbLockAndRestoreMode { get; set; } = TriggerMode.HandleWatcher;
    public int HandleWatcherStepTimeoutMs { get; set; } = 1500;
    public int TimerSeconds { get; set; } = 10;               // for Timer fallback
    public string HotkeyBinding { get; set; } = "F9";         // for Hotkey fallback
    public int PreDisableDelayMs { get; set; } = 0;
    public int InterDeviceRestoreDelayMs { get; set; } = 250; // used only when fallback mode is selected
    public DeviceRef WheelbaseDevice { get; set; }            // optional; informational for HandleWatcher, required for HidSniff fallback
}

public class DeviceRef {
    public string InstanceId { get; set; }      // primary key
    public string DeviceInterfacePath { get; set; }  // for HandleWatcher matching
    public string FriendlyName { get; set; }    // for UI display only
}

public enum TriggerMode { HandleWatcher, Hotkey, Timer, HidSniff }
```

Persist to `%LOCALAPPDATA%\GameFFBManager\profiles.json` as a list.

### 3. Single-instance + IPC

The app must run as a singleton. The shortcut and Steam wrapper paths both invoke `GameFFBManager.exe` with CLI args; if an instance is already running, the new invocation must forward its request to the running instance.

- **Mutex**: `Global\GameFFBManager-{stable-guid}` at app startup.
- **Named pipe server**: `\\.\pipe\GameFFBManager` in the main instance. JSON request/response.
- **Request types**:
  - `{"op": "launch", "profileId": "..."}`
  - `{"op": "steam-wrap-begin", "profileId": "..."}` → `{"status": "ready"}`
  - `{"op": "steam-wrap-end", "profileId": "..."}`
  - `{"op": "restore-all"}`
  - `{"op": "status"}`
- **Client mode**: send request, await response, exit (or stay alive for steam-wrap).

### 4. Process watcher (launch detection only)

Distinct from the HandleWatcher. This one runs continuously, looking for game launches that didn't go through paths 1–3.

- **Implementation**: polling. `Process.GetProcesses()` every 500ms.
- **Matching**: case-insensitive on `Process.ProcessName + ".exe"` against each enabled profile's `GameExecutableName`.
- **Suppression**: if a flow for that profile is already in progress (triggered by Launch button, shortcut, or Steam wrapper), watcher recognizes the process as already-handled and skips. `LaunchOrchestrator` exposes `IsHandling(string exeName, int processId)`.
- **Idempotency**: track `(profileId, processId)` pairs currently in a flow.
- **Concurrency**: only one flow at a time globally.
- **Known race**: process watcher fires *after* `CreateProcess` returns. Window between process creation and our `DisableDevices` call is unavoidable. For Forza (long splash/anti-cheat init), there's plenty of headroom. For fast-starting games, document that users should prefer launch paths 1–3.

### 5. Launch flow (state machine)

```
Idle
  → DisablingDevices
  → LaunchingGame      (skipped when triggered by Steam wrapper or process watcher)
  → WaitingForFfbLock
  → RestoringDevices
  → Monitoring
  → Idle
```

**DisablingDevices**
- Optional `PreDisableDelayMs` wait (default 0).
- For each device in `DisableThenRestore`, call SetupDi disable.
- Write each successful disable to the state file **before** moving on.
- If any disable fails, abort the flow, log the error, surface it.

**LaunchingGame** (manual button + shortcut path only)
- `Process.Start(profile.LaunchCommand)`. If the command starts with `shell:`, invoke via `explorer.exe`.
- Find the game process by matching `GameExecutableName`. Poll for up to 30 seconds.

**WaitingForFfbLock**
- Start the HandleWatcher (or chosen fallback trigger) targeting the resolved PID (gameinputsvc.exe if present, else game PID).
- On `HandleWatcher`: advance on first HID handle open by target PID.
- On `Hotkey` / `Timer` / `HidSniff`: as in v3.

**RestoringDevices**
- **HandleWatcher mode** (default): for each device in `DisableThenRestore` list order:
  1. Re-enable device via SetupDi.
  2. Wait for next `HidHandleOpened` event from the target PID, with `HandleWatcherStepTimeoutMs` timeout.
  3. Remove from state file.
- **Fallback mode**: re-enable in list order with fixed `InterDeviceRestoreDelayMs` sleep between each.

**Monitoring**
- Watch the game `Process` for exit. On exit, sweep the state file — anything still recorded as disabled-by-us gets re-enabled (covers user quitting before FFB lock).
- Process watcher resumes scanning normally.

### 6. Steam launch wrapper (v2)

[Same as v3 — `%command%` wrapping pattern. Wrapper IPC's main app for `steam-wrap-begin` / `steam-wrap-end`, spawns game with captured args, waits for game exit.]

### 7. Shortcut export

Profile editor provides **Create Desktop Shortcut**, **Add to Start Menu**, **Copy launch command**, *(v2)* **Copy Steam launch command**.

Shortcut implementation:
- Generate `.lnk` via `IWshShell` COM interop.
- Target: `GameFFBManager.exe`, Arguments: `--launch <profileId>`, Working directory: app install directory.
- Icon: extract from game's exe via `ExtractAssociatedIcon` or `SHGetFileInfo(SHGFI_ICON)`. Cache as `.ico` in `%LOCALAPPDATA%\GameFFBManager\icons\<profileId>.ico`.
- UAC: MVP accepts the prompt per launch. v2 stretch: optional Scheduled Task with "Run with highest privileges" + shortcut pointing at `schtasks /run` to bypass UAC.
- Generated shortcuts work for UWP games too — they invoke our app which then invokes the shell URI.

### 8. Failsafe (most important requirement)

Disabling someone's pedals and then crashing leaves them unable to drive in *any* game until they figure out Device Manager. Do not ship without this working.

- State file: `%LOCALAPPDATA%\GameFFBManager\state.json`. Schema: `{ "disabledByUs": [{ "instanceId": "...", "friendlyName": "...", "disabledAtUtc": "...", "profileId": "..." }] }`.
- Every disable writes to the state file *before* the SetupDi call; every re-enable removes from the file *after* the SetupDi call succeeds.
- On every app startup: read the state file. If non-empty, re-enable everything in it. Show recovery toast.
- Hook `AppDomain.CurrentDomain.ProcessExit`, `UnhandledException`, `Application.Current.DispatcherUnhandledException`, `Console.CancelKeyPress` → all call `RestoreAll()`.
- Permanently-visible **Restore All** button in UI, prominent if state file non-empty.

### 9. UI (WPF, single window, four tabs)

**Dashboard tab**
- Active profile dropdown.
- Two read-only lists: KeepEnabled / DisableThenRestore.
- **Launch Game** button.
- **Process Watcher** master toggle + per-profile armed indicators.
- Status line: current state-machine state + per-device status during a flow.
- **Restore All** button (always available, prominent if state file non-empty).
- Recent activity log: last ~20 events.

**Games tab**
- List of profiles.
- New / Duplicate / Edit / Delete.
- Editor:
  - Profile name.
  - Game executable picker → auto-fills `LaunchCommand` and `GameExecutableName`.
  - Manual override for both.
  - Two side-by-side lists with drag-drop between/within.
  - **Trigger mode** radio: HandleWatcher (default) / Hotkey / Timer / HID Sniff.
  - Per-mode config: step timeout (HandleWatcher), hotkey, timer seconds, wheelbase device picker (HidSniff).
  - Pre-disable delay (ms).
  - Inter-device restore delay (ms) — labeled "(only used in fallback modes)".
  - "Watch this game" checkbox (ProcessWatcherEnabled).
  - **Export** section: Desktop Shortcut, Start Menu, Copy launch command, *(v2)* Copy Steam launch command.
- Validation: `GameExecutableName` unique across enabled-watcher profiles.

**Devices tab**
- Live list of HID devices with VID/PID, instance ID, device interface path, friendly name, current state.
- Filter by usage page / usage.
- "Show all HID" toggle. Click-to-copy instance ID and interface path.

**Settings tab**
- Start with Windows. Start minimized to tray.
- Process watcher poll interval (default 500ms, range 100–2000ms).
- HandleWatcher poll interval (default 100ms, range 50–500ms).
- Default trigger mode for new profiles.
- Default hotkey binding.
- "Show all HID devices" toggle. Log retention size.
- *(v2)* "Install elevated launcher" — register Scheduled Task for UAC-free shortcuts.

### 10. P/Invoke surface

**SetupAPI** — for device enable/disable (same as v3).

**NTDLL** — for HandleWatcher:

```csharp
[DllImport("ntdll.dll")]
static extern int NtQueryInformationProcess(
    IntPtr ProcessHandle, int ProcessInformationClass,
    IntPtr ProcessInformation, int ProcessInformationLength, out int ReturnLength);

[DllImport("ntdll.dll")]
static extern int NtQueryObject(
    IntPtr Handle, int ObjectInformationClass,
    IntPtr ObjectInformation, int ObjectInformationLength, out int ReturnLength);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool DuplicateHandle(
    IntPtr hSourceProcessHandle, IntPtr hSourceHandle,
    IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle,
    uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

const int ProcessHandleInformation = 51;
const int ObjectNameInformation = 1;
```

Pattern (poll target process's handles, get names):

```
1. OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION, false, targetPid)
2. NtQueryInformationProcess(hProcess, ProcessHandleInformation, buffer, ...) → array of handle entries
3. For each handle entry:
   a. DuplicateHandle into our process (read-only)
   b. NtQueryObject(dupHandle, ObjectNameInformation, ...) with timeout (worker thread)
   c. CloseHandle(dupHandle)
   d. If name matches \Device\HID* OR a known device interface path → emit event
4. Diff against previous snapshot; emit only for new handles.
```

**Shell32** — `ExtractAssociatedIcon`, `SHGetFileInfo` for icon extraction.

**User32** — `RegisterHotKey`, `UnregisterHotKey` for Hotkey fallback trigger.

### 11. UWP / Microsoft Store game considerations

[Same as v3 — `LaunchCommand` supports both direct path and `shell:AppsFolder\<PFN>!App` URI. Process watcher catches UWP games by spawned process name. Generated shortcuts work for UWP games.]

## MVP scope

1. Device enumeration + Devices tab.
2. Profile CRUD + Games tab with editor.
3. Profile persistence to `profiles.json`.
4. Single-instance + named pipe IPC.
5. Process watcher (polling, 500ms) — safety-net trigger.
6. Manual Launch button — primary in-app trigger.
7. Shortcut export (Desktop + Start Menu) with icon extraction.
8. **HandleWatcher** — primary FFB-lock and re-enable timing mechanism.
9. **Hotkey fallback trigger** — for users where HandleWatcher misbehaves.
10. SetupDi disable + enable.
11. Failsafe state file + auto-recovery on startup + Restore All button.
12. Dashboard tab.
13. Activity log.

## v2 stretch goals

1. Steam launch wrapper (`%command%` + `--steam-wrap`).
2. Elevated launcher / Scheduled Task for UAC-free shortcuts.
3. Timer fallback FFB-lock trigger.
4. HID-sniff fallback FFB-lock trigger (HidSharp).
5. WMI `Win32_ProcessStartTrace` watcher mode (lower-latency option).
6. System tray icon, minimize-to-tray, quick-launch per profile from tray menu.
7. Profile presets library — community-contributed JSON profiles.
8. HidHide CLI backend option.
9. Crash log to local file.
10. Per-device "delay before enable" override.
11. Import/export profiles.

## Project structure

```
GameFFBManager/
  GameFFBManager.csproj
  app.manifest                   # requireAdministrator + DPI awareness
  App.xaml / App.xaml.cs         # entry point, CLI arg dispatch, mutex check
  Cli/
    LaunchInvocation.cs          # --launch <profileId> handler
    SteamWrapInvocation.cs       # --steam-wrap (v2)
    RestoreAllInvocation.cs      # --restore-all
  Views/
    MainWindow.xaml
    GameEditor.xaml
  ViewModels/
    DashboardViewModel.cs
    GamesViewModel.cs
    GameEditorViewModel.cs
    DevicesViewModel.cs
    SettingsViewModel.cs
  Services/
    DeviceEnumerator.cs          # WMI Win32_PnPEntity
    DeviceController.cs          # SetupDi P/Invoke wrapper
    ProfileStore.cs              # profiles.json
    StateStore.cs                # state.json failsafe
    ProcessWatcher.cs            # 500ms poll, launch detection
    HandleWatcher.cs             # 100ms poll, HID handle observation — CORE COMPONENT
    GameLauncher.cs              # manual launch + game-process tracking
    LaunchOrchestrator.cs        # state machine
    IpcServer.cs / IpcClient.cs  # named pipe
    ShortcutExporter.cs          # .lnk creation, icon extraction
    ActivityLog.cs
    Triggers/
      ITrigger.cs
      HandleWatcherTrigger.cs    # primary, MVP
      HotkeyTrigger.cs           # fallback, MVP
      TimerTrigger.cs            # fallback, v2
      HidSniffTrigger.cs         # fallback, v2
  Native/
    SetupApi.cs
    NtDll.cs                     # NtQueryInformationProcess, NtQueryObject
    User32.cs
    Shell32.cs
  Models/
    Profile.cs
    DeviceRef.cs
    TriggerMode.cs
    ActivityEntry.cs
    IpcRequest.cs / IpcResponse.cs
    HidHandleEvent.cs
  README.md
```

## Build / run

```
dotnet build -c Release
dotnet run
```

Ship as a single self-contained exe:
```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## CLI surface

```
GameFFBManager.exe                                  # normal UI launch
GameFFBManager.exe --launch <profileId>             # shortcut entry point
GameFFBManager.exe --steam-wrap <profileId> -- ...  # Steam wrapper (v2)
GameFFBManager.exe --restore-all                    # emergency CLI restore (no UI)
```

## Acceptance criteria

1. App launches with `requireAdministrator` UAC prompt.
2. Devices tab populates with all HID gamepad-class devices showing friendly name, VID/PID, instance ID, device interface path, current state.
3. User can create a profile via the Games tab, save it, and it persists across restart.
4. **Manual Launch button (HandleWatcher mode)**: clicking it disables devices, launches the game, detects FFB lock when game opens its first HID handle (≤500ms after enumeration), re-enables devices one at a time as the game opens handles for each, monitors process.
5. **Manual Launch button (Hotkey fallback)**: same flow but FFB-lock comes from hotkey, re-enables use fixed delay.
6. **Generated desktop shortcut**: created from profile editor, has game's icon, double-click triggers same flow as Launch button. Single-instance IPC routes correctly when app is already running.
7. **Process watcher**: launching the game by some other means (Xbox app, manual Steam click without wrapper) triggers the flow within ~1 second.
8. **No duplicate flows**: if user clicks Launch and process watcher also fires for the same launch, only one flow executes.
9. **HandleWatcher target PID selection**: when `gameinputsvc.exe` is running, HandleWatcher targets that PID. When it isn't, HandleWatcher targets the game PID. Verified by watching log output during FH5 (uses gameinputsvc) vs iRacing (uses game directly).
10. **HandleWatcher timeout fallback**: if no handle open is detected for `HandleWatcherStepTimeoutMs`, flow advances anyway with a warning logged.
11. **Failsafe**: killing the app mid-flow leaves devices disabled. Restarting the app re-enables them and shows recovery toast.
12. **Restore All button** works whether or not a flow is in progress.
13. Tested with at least 4 HID gamepad devices simultaneously connected.

## Out of scope

- Any code that runs inside the game process. No DLL injection, no API hooking, no module loading into the game.
- Kernel-mode driver development. Usermode admin process is sufficient.
- Cross-platform support. Windows-only.
- Modifying game files, registry settings owned by the game, or game memory.
- Anti-cheat evasion of any kind.
- Suspending or otherwise pausing the game process (IFEO debugger redirection, `NtSuspendProcess`-based interception) — anti-cheat risk too high.
- Reading game process memory or attaching as a debugger. HandleWatcher only inspects the kernel handle table, which is observable without any debug rights on the target — only the standard `PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION` access rights, granted to any admin process.
