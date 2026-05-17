# HID Reorder

A per-game HID device profile manager for sim racing and controller games. Define profiles that automatically disable interfering devices before a game launches — so your wheel base always gets enumerated first — then re-enable them one by one as the game picks them up.

Think HidHide, but per-game and automatic instead of a global toggle.


---

## The problem

Forza Horizon 5/6 (and others) assign FFB output to whichever HID game controller enumerates first at startup. If Windows enumerates your handbrake or pedals before your wheel base, you get zero FFB. The "unplug everything and replug your wheel first" community fix is solving exactly this — it's just tedious to do every session.

Same problem affects iRacing setups with many devices, older Codemasters titles, Dakar Rally, and any game that gets confused by multiple controllers.

---

## How it works

Each **game profile** defines three device categories:

| Category | What it does |
|---|---|
| **Keep Enabled** | Never touched — your wheel base goes here |
| **Disable → Re-enable** | Disabled before launch; re-enabled one by one as the game opens HID handles |
| **Keep Disabled** | Disabled for the whole session; re-enabled only when the game exits (for controller games where you want the sim rig hidden entirely) |

Unassigned devices are ignored — the profile only touches what you explicitly put in a list.

The re-enable sequence uses **handle watching**: the app monitors which HID handles the game process opens, and re-enables the next device only after the game acknowledges the previous one. No fixed timers, no guessing.

---

## Requirements

- Windows 10 or 11
- Administrator rights — one UAC prompt on launch, then you're done for the session

---

## Getting started

Download `HIDReorder.exe` from [Releases](../../releases) and run it.

### Games tab

Create a profile per game:

1. Click **+** to create a new profile
2. Set a name and browse to the game's `.exe`
3. In **Detected Devices**, select your wheel base → click **→ Keep Enabled**
4. Select pedals, handbrake, etc. → click **→ Disable → Re-enable** (in the order you want them restored)
5. For controller games: put your whole sim rig under **→ Keep Disabled**
6. Choose a re-enable trigger (Handle Watcher recommended; Timer as fallback)
7. Click **Save Profile**

### Launching

Three ways to trigger a profile:

- **In-app Launch button** (Dashboard tab) — good for testing
- **Steam Launch Options** — paste the generated command (`%command%` wrapper) so Steam handles it automatically every time
- **Desktop shortcut** — generated from the Games tab for non-Steam games

### Devices tab

Live list of all detected HID devices. Click the ON/OFF button to enable or disable individual devices directly. Click an instance ID to copy it. Toggle "Show all HID" to see keyboards, mice, and other non-gamepad devices.

---

## Identifying your devices

Most devices enumerate with generic names. The VID in brackets tells you who made it:

| Brand | VID |
|---|---|
| MOZA Racing | `VID_346E` |
| Simagic | `VID_3670` |
| Fanatec | `VID_0EB7` |
| Heusinkveld | `VID_30B7` |
| Simucube / Granite Devices | `VID_16D0` |
| Thrustmaster | `VID_044F` |
| Logitech | `VID_046D` |
| Asetek SimSports | `VID_2433` |
| Cammus | `VID_3416` |
| STM32 devices (Cube Controls, VRS, etc.) | `VID_0483` |

Can't tell devices apart? Unplug one, hit Refresh, see what disappears.

VID not listed? Look it up at [usb-ids.gowdy.us](https://usb-ids.gowdy.us/) and add it to `vid-names.json` via PR.

---

## Forza Horizon 5 / 6 specific notes

- FH5/FH6 route HID through `gameinputsvc.exe` — the app watches that process, not the game itself
- MOZA wheels need **Forza Compatibility Mode** enabled in MOZA Pit House — this makes the base present as a Fanatec device (VID `0EB7`), which FH6 detects via its native Fanatec SDK
- Put your MOZA base in **Keep Enabled**, everything else in **Disable → Re-enable**

---

## Troubleshooting

**FFB still not working**
Make sure you launch the game through this app (or the Steam wrapper), not directly. The devices need to be disabled *before* the game starts enumerating.

**A device didn't come back**
Hit **Restore All** in the Dashboard tab — it re-enables everything the app has disabled. Also runs automatically on next app launch if the app crashed mid-session.

**Device not showing up in the list**
Hit Refresh. If it still doesn't appear, try enabling "Show all HID" — it may be enumerated under an unexpected class. vJoy and ViGEm virtual devices are supported.

---

## Contributing

PRs welcome for:
- VID/PID entries for brands not in `vid-names.json`
- Bug fixes or improvements
- Community game profiles

---

## Project layout

```
/app             — WPF app (active development)
/gui             — original WinForms prototype (preserved, not extended)
/vid-names.json  — shared VID/PID vendor name map
```

---

## Credits

- VID data curated for sim racing hardware; upstream database at [github.com/gregkh/usbutils](https://github.com/gregkh/usbutils)
