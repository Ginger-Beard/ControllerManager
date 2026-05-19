# User Criteria — Device Hiding & HidHide Integration

Reference this alongside TODO.md. These are the agreed UX requirements for the HidHide
integration as it currently ships. Do not deviate without updating this file.

**History note (2026-05-19):** the original draft of this file kept a pnputil fallback
on the table. pnputil and its associated services have since been removed entirely;
HidHide is the only backend. HandleWatcher is also being removed (EAC kills the process
for the DuplicateHandle pattern — see TODO Recent decisions). Sections below have been
updated to match.

---

## Core principle

**All processes see all devices by default.**
Hiding only happens when explicitly triggered — either by a game profile launching,
or by a manual toggle in the Devices tab. When nothing is active, HidHide's filter
is off and the system is completely transparent.

---

## HidHide as backend

### Installation
- HidHide is a **required** prerequisite, not bundled (we don't ship the kernel driver)
- If not installed: Settings tab shows a "✗ Not installed — Download ↗" link; the
  Devices/Games tabs still render but hiding is a no-op.
- If installed: use HidHide automatically, no user configuration required.
- No alternate backend — pnputil and its fallback UI were removed 2026-05-19.

### Companion apps (Pit House, SimHub, GHub, Synapse, etc.)
- **Zero whitelist configuration required, ever.**
- Achieved via HidHide inverse whitelist mode: only the game process is blocked,
  all other processes retain full access regardless.
- Users must never be asked "which apps should still see your devices."

### Default state (no game running)
- HidHide Active = false
- All processes see all devices
- No entries in session blacklist

---

## Game profile behaviour

### On game launch
- Session blacklist populated with the profile's Disable and Keep Disabled devices
- Game exe path added to inverse whitelist (only this process gets blocked)
- HidHide Active = true
- Result: game cannot see profile-hidden devices; everything else can

### During game session
- Companion apps (Pit House, SimHub, etc.) retain full device access — no action needed
- HidHide session blacklist is owned by Controller Manager process; auto-cleans if app crashes

### On game exit
- Session blacklist cleared (explicit clear or auto-cleanup on our process exit)
- Inverse whitelist cleared
- HidHide Active = false
- All processes see all devices again immediately — no reboot

### Profile creation UI (current shape as of 2026-05-19)
- Single ordered device list per profile with per-row role selector:
  - **Always Visible** — game sees this from the start (was "Keep Enabled")
  - **Reveal After Start** — hidden at launch, revealed sequentially after game stabilizes
  - **Always Hidden** — never visible to this game
- Per-device `DelaySeconds` field on RevealAfterStart rows (pause after revealing this
  device, before the next one in the list)
- Profile-level `InitialDelaySeconds` (default 5s) — wait between game launch and start
  of reveal sequence. Replaced the old `TriggerMode` + `TimerSeconds` fields.
- Up / Down / Remove buttons per row; reveal order is stable across launches so
  in-game slot assignment is consistent.
- JSON keys (`keepEnabled` / `disableThenRestore` / `keepDisabled`) preserved for
  backwards compatibility with old profiles.

---

## Devices tab behaviour

### Individual device on/off toggle
- Kept as-is — users can manually toggle individual devices
- With HidHide backend: toggle OFF adds device to **persistent** HidHide blacklist
  and activates HidHide (all non-whitelisted processes lose access to that device)
- With pnputil backend: toggle OFF disables the device system-wide via PnP (same as today)
- Distinction is visible to power users: HidHide = access denied but device still
  enumerable; pnputil = device fully disabled in Device Manager

### Global on/off toggle
- A master switch that enables/disables ALL hiding at once
- With HidHide backend: maps directly to `IOCTL_SET_ACTIVE` (boolean)
  - Off → HidHide filter bypassed entirely, all processes see all devices
  - On → current blacklist enforced
- With pnputil backend: not applicable (no equivalent — individual toggles only)
- Expose this in the Devices tab as a clearly labelled master toggle

### Devices tab and game profiles coexist
- Persistent blacklist (Devices tab toggles) and session blacklist (game profiles)
  are independent — HidHide merges them internally
- A device toggled OFF in the Devices tab stays OFF even during a game session
- A device hidden by a game profile is only hidden for that session; Devices tab
  toggle state is unaffected

---

## Behaviour when HidHide is not installed

- Settings tab shows "✗ Not installed" with a Download link.
- All device-hiding operations become no-ops; the orchestrator still runs but no
  hiding/revealing actually happens (the game sees all devices).
- No silent fallback to a different backend — pnputil is gone.

---

## What does NOT change

- Steam wrapper (`--steam-wrap`) — identical
- Shortcut launch — identical
- Process watcher / auto-trigger — identical
- Dashboard Launch button — identical
- System tray quick-launch — identical
- Profile JSON file format — identical (old `triggerMode` / `timerSeconds` fields
  are read for migration on load and then no longer written)
