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
| **Keep Disabled** | Disabled for the whole session; re-enabled only when the game exits |

Unassigned devices are ignored.

The re-enable sequence uses **handle watching**: the app monitors which HID device handles (or DirectInput registry keys) the game process opens, and re-enables the next device only after the game has acknowledged the previous one. No fixed timers, no guessing.

---

## Requirements

- Windows 10 or 11
- Administrator rights — one UAC prompt on launch, then you're done for the session

---

## Getting started

Download `HIDReorder.exe` from [Releases](../../releases) and run it.

### Games tab — create a profile

1. Click **+** to create a new profile
2. Enter a name and browse to the game's `.exe`
3. In the **Detected Devices** picker, assign each device to a category:
   - Wheel base → **Keep Enabled**
   - Pedals, handbrake, shifter → **Disable → Re-enable** (drag to set restore order)
   - Sim rig devices for controller games → **Keep Disabled**
4. Choose a **re-enable trigger**:
   - **Handle Watcher** (recommended) — re-enables each device as the game opens its handle
   - **Timer** — re-enables after a fixed delay
5. Click **Save Profile**

### Launching

**Option 1 — Steam Launch Options (recommended for Steam games)**

Click **Copy Steam Command** in the Games tab and paste it into Steam → right-click game → Properties → Launch Options. From then on, launching from Steam automatically runs the full disable → launch → re-enable flow.

**Option 2 — Desktop / Start Menu shortcut**

Click **Create Desktop Shortcut** or **Add to Start Menu** in the Games tab. Double-clicking the shortcut triggers the same flow.

**Option 3 — In-app Launch button**

Select a profile on the Dashboard tab and click **🎮 Launch**. Good for testing.

**Option 4 — Process watcher (automatic safety net)**

If the process watcher is enabled (Settings tab), the app detects when a configured game launches by any means and triggers the flow automatically. There's a known race window — prefer options 1–3 for games where enumeration timing is tight.

### Dashboard tab

Shows the active profile's device lists at a glance, status during a flow, and an activity log. The **Restore All** button re-enables every device the app has disabled — use it if something goes wrong.

### Devices tab

Live list of all detected HID devices. Toggle individual devices on/off directly. Click an instance ID to copy it. Enable **Show all HID** to see keyboards, mice, and other non-gamepad devices.

---

## Forza Horizon 5 / 6 specific notes

- FH5/FH6 read DirectInput calibration registry keys at startup — the Handle Watcher detects this as the acquisition signal. You can verify it using the debug panel in the Games tab.
- MOZA wheels need **Forza Compatibility Mode** enabled in MOZA Pit House — this makes the wheel present as a Fanatec device (VID `0EB7`), which FH6 detects via its native Fanatec SDK and routes FFB correctly.
- Put your MOZA base in **Keep Enabled**, everything else in **Disable → Re-enable**.

**Use Timer mode, not Handle Watcher, for the re-enable sequence.**
FH6 initializes its controller stack once at startup and does not re-scan when devices are re-enabled mid-session. Handle Watcher will successfully detect the acquisition signal (FH6 writing its DirectInput registry keys), but all per-device re-enable steps will time out because the game never opens new handles to the re-enabled devices. Set the trigger to **Timer** and `TimerSeconds` to roughly how long FH6 takes to reach the main menu — 20–30 seconds is a reasonable starting point. The devices will be re-enabled in sequence after that delay regardless of whether the game acknowledges each one.

---

## Identifying your devices

Most devices enumerate with generic Windows names. The VID in brackets tells you the manufacturer:

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

## Troubleshooting

**FFB not working after launch**
Make sure the game launches through HID Reorder (Steam command, shortcut, or Launch button) — not directly. The devices must be disabled *before* the game starts enumerating.

**A device didn't come back**
Click **Restore All** on the Dashboard tab. It also runs automatically the next time the app starts if it crashed mid-session.

**No devices showing in the list**
Hit Refresh. Enable **Show all HID** — the device may be enumerated under an unexpected class. vJoy and ViGEm virtual devices are supported.

**Handle Watcher showing nothing**
Use the debug panel in the Games tab (expand "Handle Watcher"). Check the poll stats line — if it shows handles being scanned, the watcher is working. Enable **All handles** to see all named handles and confirm the game process is being watched correctly.

**Handle Watcher times out on every device during re-enable**
This is expected for games that initialize DirectInput once at startup and don't re-scan (Forza Horizon 5/6 is the main example). The watcher can detect the acquisition signal fine, but the game won't open new handles when individual devices are re-enabled mid-session. Switch the profile to **Timer** mode — see [Forza Horizon 5 / 6 specific notes](#forza-horizon-5--6-specific-notes).

---

## Contributing

PRs welcome for:
- VID/PID entries in `vid-names.json`
- Bug fixes or improvements
- Community game profiles

---

## Project layout

```
/app             — WPF app
/vid-names.json  — VID/PID vendor name map
```

---

## Credits

- VID data curated for sim racing hardware; upstream database at [github.com/gregkh/usbutils](https://github.com/gregkh/usbutils)
