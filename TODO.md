# TODO — Controller Manager

Reference this at the start of any session continuing work on this project.
Also reference **CRITERIA.md** for the agreed UX requirements.

---

## What this tool is

A per-game HID device profile manager for sim racing and controller games. Profiles
say which devices a given game should see — what's visible from the start, what gets
revealed later (and when), and what stays hidden the whole session. Built on top of
HidHide as the kernel-level device-hiding backend.

Three roles per device per profile:
- **Always Visible** — the game sees it from launch (your wheel base, your gamepad)
- **Reveal After Start** — hidden at launch, revealed at a configured T+Xs absolute
  time. List order is the reveal order.
- **Always Hidden** — never visible to this game

Devices not in the profile are also hidden from the game for the duration of the
session.

---

## Architectural ground truth

### FFB / slot-ordering fix
Forza Horizon games and some other titles assign FFB and controller slot #1 based on
which gaming HID device is visible to DirectInput first at startup. HidHide hooks
`PsSetLoadImageNotifyRoutine`, so deny-list rules are in effect before any game code
runs — no race window. The startup scan only sees Always-Visible devices; reveal
phase brings the rest online afterward.

### Reveal phase is timer-based (HandleWatcher gone)
HandleWatcher (`NtQueryInformationProcess` + `DuplicateHandle` loop on the game's
handle table) was removed because EAC and similar anti-cheats kill the process for
the pattern. Reveal timing is now a fixed `T+Xs` absolute number per device in
`DeviceRef.DelaySeconds`. Default for a fresh Reveal-After-Start device is 5s
(enough for FH-style startup scans to commit slot #1 to the wheel).

### Reveal phase limit
Works only for hot-plug-aware games (WGI / RawInput / modern XInput). Pure legacy
DirectInput games that do a one-shot startup enumeration can have FFB *or*
late-revealed devices but not both — HidHide doesn't fire `DBT_DEVICEARRIVAL` when
a device leaves the session blacklist. Calling this out honestly in the README is
worth more than trying to work around it.

### HidHide driver compatibility — 1.4.181.0
The stock signed driver doesn't ship the session-blacklist IOCTLs
(`0x80016020/24`); those exist only in the modified source in our `HidHide/` repo.
We work around by snapshot-and-restore the persistent blacklist around each session
(see `HidHideClient.BeginGameSession` / `EndGameSession`). If CM crashes mid-session,
session devices remain in the persistent BL until manually re-enabled from the
Devices tab.

### Four supported use cases (settled)
1. **Forza Horizon / Xbox sim racing** — wheel = AlwaysVisible, pedals/shifter =
   RevealAfterStart at staggered T+Xs times, gamepad = AlwaysHidden.
2. **Sim racing companion apps** (SimHub, Pit House, GHub, Synapse) — automatically
   retain device access via HidHide inverse-whitelist mode (only the game's exe is
   in the deny list during a session). No allow-list config needed.
3. **Other PC games with controllers** — gamepad = AlwaysVisible, sim rig = AlwaysHidden.
   No RevealAfterStart entries; orchestrator skips the wait+reveal phase entirely.
4. **Sunshine / Apollo streaming** — virtual gamepad = AlwaysVisible (build the profile
   while a remote session is active so the dynamic device appears in the picker),
   everything else = AlwaysHidden.

### IOCTL reference (kept for future driver work)
- Control device: `\\.\HidHide`, open with `GENERIC_READ`
- Formula: `CTL_CODE(32769, f, METHOD_BUFFERED, FILE_READ_DATA)` = `0x80014000 | (f << 2)`
- `GET/SET_WHITELIST`  (0x80016000/04) — NT device paths, multi-string
- `GET/SET_BLACKLIST`  (0x80016008/0C) — device instance paths, multi-string
- `GET/SET_ACTIVE`     (0x80016010/14) — BOOLEAN
- `GET/SET_WLINVERSE`  (0x80016018/1C) — BOOLEAN; true = whitelist acts as deny-list

### Profile schema versions
- v0 — legacy: `InitialDelaySeconds` + per-device `DelaySeconds` = "wait AFTER reveal"
- v1 — `DelaySeconds` = "wait BEFORE reveal" (relative)
- v2 — current: `DelaySeconds` = absolute "reveal at T+Xs from game launch"

`ProfileEditorViewModel.LoadProfile` migrates 0 and 1 to 2 on read; `ToProfile`
always writes v2.

---

## Open work

### Composite HID device handling
Devices tab rows can dedupe multiple HID interfaces (MI_00 + MI_01) into one display
row, but `DevicesViewModel.ToggleEnabled` only blacklists the primary `InstanceId`
when toggled off. Child interfaces stay accessible to all processes → device only
partially hidden. Fix: `HidDevice` should track the full set of related interface
instance IDs (currently we have `AlternativeInstanceId` as a single slot from the
pnputil era — not enough); `ToggleEnabled` adds all of them to / removes all from
the BL. Not blocking sim racing (most game controllers expose one interface) — defer
until a real device hits it.

### UAC-free Steam / shortcut launch
`ControllerManager.exe` is `requireAdministrator`. Steam and `.lnk` shortcuts that
target it trigger a UAC prompt on every launch — even when CM is already running in
the tray (second instance still elevates before forwarding the IPC, then exits).

Fix: create a Scheduled Task with "Run with highest privileges," then change
`ShortcutExporter` to point shortcuts at `schtasks /Run /TN <task>` (with the profile
ID passed via task argument or environment variable) instead of the exe directly.
Same trick `Start with Windows` already uses for the boot launch.

### Input monitor — controller triggers + visuals
HID analog triggers reportedly don't display correctly in the input monitor (need a
real controller to repro). Joystick X/Y scatter visualization, center-zero drift
viz, and stick visuals are still backlog. Code lives in `HidInputMonitor.cs` +
`DevicesViewModel.UpdateMonitor`.

### Idle / standby device profile
A "no game running" default profile: devices listed in it stay hidden until a game
profile takes over, then restore to idle state (not all-enabled) when the game
exits. Use case: keep the entire sim rig invisible to other apps by default, only
surface devices when a sim game runs. Open design questions:
- Modeled as a special profile, or a separate layer on top of the persistent BL?
- Does game-profile exit restore to idle state or to all-enabled? (Currently the
  latter — needs unifying with idle if it ships.)
- Activates on app start, or only after the first game session ends?

---

## Ship readiness

### README rewrite
Still titled "HID Reorder" and written as internal docs. Needs a user-facing setup
guide:
- Plain-language "what it does and why," lead with the FFB problem
- Step-by-step setup with screenshots
- Real game examples (FH5 / FH6 confirmed, others marked unverified — strip anything
  hallucinated)
- Sunshine / Apollo cookbook (use Sunshine's "Command Preparations" → cmd field with
  `"...\ControllerManager.exe" --launch <profileId>`; detach command runs the same
  on session end or relies on process watcher)
- Download options: slim (needs .NET runtime) vs self-contained (bigger, runs
  anywhere) — most users should grab self-contained
- Honest limit: hot-plug-aware games only for the reveal phase

### Icon
Current `.ico` is a placeholder. Replace `app/app.ico` with a real multi-size icon
(16 / 32 / 48 / 256). Direction: device-manager-like, since the tool is now framed
as a controller-focused Device Manager.

### Code signing
Apply to SignPath.io (free for OSS, GitHub Actions integration, multi-day approval
turnaround). Microsoft Trusted Signing on Azure is the paid alternative (~$10/mo,
faster). Until signed, SmartScreen warns on first run.

---

## Testing checklist (no code; do before each release)

- FH6 end-to-end: confirm EAC crash is gone with the timer-based reveal, FFB on the
  wheel, pedals/shifter reveal in the expected order at the configured T+Xs times
- Sunshine/Apollo: build a profile while a remote session is active, virtual gamepad
  shows up in the picker, hidden physical controllers stay hidden during stream
- Profile ID healing: rename a device's instance ID (USB reseat, port change),
  reopen the profile in Games tab — `ProfileHealer` should rewrite it silently and
  show the orange status banner
- Devices tab toggle: verify on/off persists across app restarts; verify the BL is
  cleared cleanly on `Hide all → ON, then OFF` cycle

---

## Build notes

- Build: `dotnet.exe build` from `/app` (Windows binary, not WSL `dotnet`)
- Self-contained publish: `dotnet.exe publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish-sc`
- Slim publish: `dotnet.exe publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -o publish-fd`
- Release: push a `vX.Y.Z` tag to trigger GitHub Actions (builds both)
