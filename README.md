# HID Reorder

Fix for games (Forza Horizon 6, others) that send force feedback or rumble **only to the first enumerated HID game controller**. If Windows enumerates your wheel base second, you get zero FFB.


---

## How it works

Windows assigns game controller slot numbers in the order devices enumerate at boot. That order can shift after any reboot, USB reconnect, or driver update. This tool:

1. Disables all your sim racing HID devices simultaneously
2. Re-enables your wheel base first — it claims slot #1
3. Re-enables everything else in the order you set

Devices are matched by USB Vendor ID (VID) and Product ID (PID), so it doesn't matter which physical port anything is plugged into.

---

## Requirements

- Windows 10 or 11
- Administrator rights — one UAC prompt on launch, then you're done for the session

---

## Getting started

Download `HidReorder.exe` from [Releases](../../releases) and run it. UAC will prompt for admin — that's required to disable and re-enable devices.

### Device Order tab

Detected sim devices populate automatically. From here:

- **Drag** the handle on the left to reorder — the top device becomes slot #1
- **Check/uncheck** devices to include or exclude them. Unchecked devices get disabled and stay disabled, which is useful for hiding devices from games entirely without needing HidHide
- **Save named profiles** for different setups — sim rig, couch gaming, whatever. Select a profile from the dropdown to apply it, type a new name and hit Save to create one
- Hit **Reorder Devices** — everything briefly disconnects and comes back in the order you set

Run this before launching your game each session.

### Drift Monitor tab

Shows live axis readings across all detected joystick devices. Any axis sitting more than N% from center gets flagged red. Use this to find which device is sending constant input and interfering with your games.

Common causes: dirty potentiometer, needs calibration via `joy.cpl`, or the device's own software needs a deadzone set.

---

## Identifying your devices

The app auto-detects game controllers but most show up with generic names like "HID-compliant game controller". The VID (Vendor ID) in brackets tells you who made it. Here are the VIDs for common sim racing brands:

| Brand | VID | Notes |
|-------|-----|-------|
| MOZA Racing | `VID_346E` | All bases |
| Simagic | `VID_3670` | Alpha, GT4, DX8, handbrakes |
| Fanatec | `VID_0EB7` | All Fanatec devices |
| Heusinkveld | `VID_0483` | STM32 chip — see note |
| Simucube / Granite | `VID_16D0` | SC1, SC2 |
| Thrustmaster | `VID_044F` | All wheels |
| Logitech | `VID_046D` | G29, G923 |
| Asetek SimSports | `VID_2433` | Invicta, Forte |
| Cube Controls | `VID_0483` | STM32 — use PID to distinguish |
| Cammus | `VID_3416` | C5, C12 |
| VRS DirectForce | `VID_0483` | STM32 — use PID to distinguish |

> **STM32 note:** `VID_0483` is STMicroelectronics' chip, used by Heusinkveld, Cube Controls, VRS, and others. If you have multiple `VID_0483` devices they'll show as separate entries distinguished by PID.

**Can't figure out which device is which?** Unplug one, hit Refresh, see what disappears. Repeat.

**VID not showing a name?** Look it up at [usb-ids.gowdy.us](https://usb-ids.gowdy.us/) and add it to `vid-names.json` via PR.

---

## Troubleshooting

**FFB still not working after reordering**
Run before launching the game, not after. Some titles also need the wheel re-assigned in in-game settings after a reorder.

**A device didn't come back**
Wait 5–10 seconds — some devices are slow to enumerate. Still missing: unplug and replug, then hit Refresh. If you unchecked it intentionally, check it and reorder again to bring it back.

**My wheel base VID isn't in the table**
Look it up at [the-sz.com/products/usbid](https://www.the-sz.com/products/usbid/). Or check the manufacturer's own GitHub or forums — most publish their VID.

---

## Contributing

PRs welcome for:
- Confirmed VIDs for brands not in the table — add to `vid-names.json`
- Bug fixes or improvements to the app
- Better device detection or naming

---

## Credits

- VID data in `vid-names.json` was manually curated for sim racing hardware. The broader community-maintained USB vendor ID database lives at [github.com/gregkh/usbutils](https://github.com/gregkh/usbutils) — if you're looking up an unknown VID or want to contribute one upstream, that's the place
