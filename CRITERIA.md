# User Criteria — Device Hiding & HidHide Integration

Reference this alongside TODO.md when implementing the HidHide backend.
These are the agreed UX requirements. Do not deviate without updating this file.

---

## Core principle

**All processes see all devices by default.**
Hiding only happens when explicitly triggered — either by a game profile launching,
or by a manual toggle in the Devices tab. When nothing is active, HidHide's filter
is off and the system is completely transparent.

---

## HidHide as backend

### Installation
- HidHide is an **optional** prerequisite, not bundled
- If not installed: fall back to pnputil silently, show a banner in Settings
- If installed: use HidHide automatically, no user configuration required
- Banner in Settings tab only:
  ```
  Device hiding backend
    ● HidHide (recommended)   ✓ Installed
    ○ Basic (pnputil)  [Deprecated]   Some devices may require reboots
  ```
  With a download link when not installed. Nothing else changes in the UI.
- The pnputil option is labelled **Deprecated** in the UI (greyed tag next to the label).
- Selecting pnputil triggers a confirmation dialog before the setting is applied:
  > **Switch to legacy backend?**
  >
  > The basic (pnputil) backend disables devices at the driver level rather than
  > filtering access. If something goes wrong mid-session, devices may appear
  > disabled in Device Manager and require manual re-enabling via Device Manager
  > or a reboot to recover.
  >
  > HidHide is strongly recommended. Only switch if you have a specific reason.
  >
  > [Switch anyway]  [Cancel]
- If the user clicks Cancel, the radio button snaps back to HidHide — no setting change.
- This dialog only appears when actively switching TO pnputil, not on every settings open.

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

### Profile creation UI
- **No changes.** Same three device roles (Keep Enabled / Disable→Re-enable / Keep Disabled)
- Same Games tab, same device lists, same profile editor
- No new fields, no new concepts for the user

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

## pnputil fallback behaviour (HidHide not installed)

- Everything works as it does today
- MOZA wheel and similar drivers with the exit-3010 quirk: one game session works
  cleanly (disable → game → restore). A second session in the same Windows session
  may require a reboot — user sees a clear error message, not a silent failure
- Settings banner prompts user to install HidHide to resolve this
- No other degraded behaviour

---

## What does NOT change

- Steam wrapper (`--steam-wrap`) — identical
- Shortcut launch — identical
- Process watcher / auto-trigger — identical
- Dashboard Launch button — identical
- Profile structure and JSON format — identical
- HandleWatcher — still used for re-enable sequencing on pnputil backend;
  not needed for HidHide (device was never disabled, just access-denied)
- System tray quick-launch — identical
