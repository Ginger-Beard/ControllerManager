# Controller Manager

**Stop fighting Windows about which controller your game sees.** Per-game profiles
that hide unwanted devices, restore FFB to your wheel, and keep your sim rig and
your gamepad from stepping on each other — built on top of [HidHide](https://github.com/nefarius/HidHide).

> Windows. It works for me — feel free to read the code, improve it, or do it
> better. PRs welcome. I won't be helping anyone set it up individually.

---

## What it actually solves

If any of these sound familiar, this app is for you:

- **Forza Horizon 5/6 (and Motorsport):** no force feedback even though everything
  else works. Cause: the game assigns FFB to whichever controller it sees first at
  startup, and Windows usually picks your pedals or shifter instead of your wheel.
- **iRacing / ACC / Le Mans Ultimate with a big rig:** you reseat the USB hub every
  session because devices get reassigned to different controller slots, breaking
  your bindings.
- **Steam game refuses to use your gamepad** because your wheel is plugged in and
  it grabbed slot #1.
- **You stream games to a remote device** (Sunshine/Apollo) and the host's physical
  controllers fight the virtual one for slot #1.

The community fix has always been "unplug everything, plug your wheel in first."
This app does that for you, per game, automatically — at the driver level, without
actually unplugging anything.

---

## How it works in plain language

Every game profile is a short list of your devices, each tagged one of two ways:

- **Always Visible** — the game sees this from the moment it launches.
  *(Your wheel base goes here. Your gamepad goes here for non-racing games.)*
- **Reveal After Start** — hidden when the game starts, then revealed at a time you
  choose (e.g. "5 seconds after launch"). Order matters — first revealed gets the
  next available controller slot.
  *(Your pedals and shifter usually go here for FFB games, so the wheel grabs slot
  #1 alone.)*

**Anything not in the profile is hidden from the game.** That's how you keep your
gamepad out of a racing game, or your sim rig out of a controller game — just don't
add it. No "hidden" role needed; absent = hidden.

**Other apps see everything normally.** SimHub, Pit House, MOZA, Fanatec drivers,
Logitech G HUB — all of them keep full access to all your devices during a game
session. You don't have to whitelist anything.

---

## Install

You need both:

1. **[HidHide](https://github.com/nefarius/HidHide/releases/latest)** — install the
   MSI from the Releases page. It's a signed kernel driver from
   [Nefarius](https://github.com/nefarius); it's what actually does the hiding.
2. **Controller Manager** — grab the latest from this repo's
   [Releases](../../releases). Two builds:
   - **Self-contained** — bigger download, works on any Windows 10/11 with no
     other prerequisites. *(Pick this one if you're not sure.)*
   - **Slim** — smaller, but needs the .NET 10 Desktop Runtime installed.

Run it. You'll get a UAC prompt once on launch (it needs admin to talk to HidHide's
kernel driver). If HidHide isn't installed yet, Controller Manager pops a dialog
on first launch with a direct link to the Releases page — install it, then restart
the app. After that, you're set — toggle **Start with Windows** in Settings and
it'll be in the tray every time you boot, no further prompts.

---

## Use cases

### ① "I want FFB in Forza Horizon"

The classic case.

1. **Games tab → New profile** — name it "Forza Horizon 6", browse to
   `forzahorizon6.exe` (Steam: `...steamapps\common\ForzaHorizon6\forzahorizon6.exe`).
2. Add each device you want the game to see by clicking **+ Add** (or
   double-clicking the row) in the device picker at the bottom: wheel,
   pedals, shifter, handbrake, button box. **Don't add the gamepad** (or
   anything else you don't want the game touching — it'll be hidden
   automatically).
3. Set roles in the device list:
   - Wheel base → **Always Visible**
   - Pedals → **Reveal After Start**
   - Shifter → **Reveal After Start**
   - Handbrake → **Reveal After Start**
4. Check **Wait until the game opens the first device before revealing the
   rest**. Leave **Wait this many seconds after:** at the 1.5s default.
5. Set the per-row **Reveal at** times as additional offsets *after* that
   grace period. Common pattern: pedals=0s, shifter=1s, handbrake=2s.
   So with grace=1.5s, the pedals reveal 1.5s after the wheel opens, shifter
   at 2.5s, handbrake at 3.5s — each gets its own slot.
6. Drag the Reveal After Start rows (☰ handle) into the order you want them
   mapped to in-game slots #2, #3, #4. List order = reveal order.
7. Click **▶ Explain this profile** above the device list to verify the
   launch timeline matches what you expect. Then **Save Profile**.

When FH6 starts, only the wheel is visible. The game does its warmup, opens
the wheel file when it's ready, and Controller Manager spots that exact moment
via kernel ETW — then waits 1.5 seconds (your "post-acquisition delay") so the
game has time to lock the wheel as controller slot #1 before revealing the
rest.

> **MOZA tip:** turn on **Forza Compatibility Mode** in Pit House so the wheel
> presents as a Fanatec (VID `0EB7`). FH6 detects Fanatec directly via its native
> SDK and routes FFB through it cleanly.

> **If your wheel still doesn't get slot #1:** bump **Wait this many seconds
> after:** to 2.0 or 2.5 seconds. Some games take longer to commit slot
> assignment after first noticing a controller. Worst case 3s — beyond that,
> something else is going wrong and worth filing an issue.

> **If reveals never happen at all:** the game probably uses Windows'
> GameInput (Forza Horizon and modern Xbox-style PC games do) — Controller
> Manager can't see the "game opened the wheel" moment for those titles.
> After a 60-second timeout, Controller Manager falls back to revealing at
> absolute times, but by then your game has long since stopped scanning.
> The fix: **uncheck the "Wait until..." box** for those games, switch to
> Timer mode, and use the per-row **Reveal at** values as absolute seconds
> from game launch (e.g. 11.0s, 11.1s, 11.2s for Forza).

#### Forza + lots of peripherals: consolidate with vJoy

Forza's multi-USB handling is fragile once you start adding peripherals
beyond the basics. In testing, profiles with a wheel + pedals + shifter +
handbrake + button box (5+ HID devices) saw the game lose track of devices,
misassign slots, or ignore inputs entirely — even after Controller Manager
got the reveal timing right. Forza only reliably handles a handful of
devices at once; piling on a full sim rig pushes past what the engine wants
to deal with.

The clean workaround used by experienced sim racers is **input consolidation
via vJoy + SimHub's Control Mapper**:

1. Install [vJoy](https://github.com/njz3/vJoy/) and bump button count from
   the default 8 to 32 in vJoy Configurator (you'll need it).
2. In SimHub, open **Control Mapper** and create mappings from each physical
   input onto a single vJoy device:
   - Foot pedals (brake, throttle, clutch) → vJoy axes
   - Wheel paddles (clutch, up/down shift) → vJoy buttons
   - Shifter, handbrake, button box → vJoy buttons
   - **Leave steering on the wheel** — don't route it through vJoy.
3. In the Controller Manager profile for Forza:
   - Wheel base → **Always Visible**
   - vJoy device → **Always Visible**
   - **Don't add** the physical pedals, shifter, handbrake, or button box —
     leaving them out of the profile hides them from Forza automatically.
4. In Forza's controller settings, bind everything to the consolidated vJoy
   device (steering and FFB stay on the wheel).

Result: Forza sees exactly **two** devices — the wheel and vJoy. No conflicts,
no slot confusion, and no dual-source ambiguity (where the same pedal reports
through both its native wheel axis and a vJoy axis). Steering and FFB stay
on the wheel where they belong; everything else funnels through vJoy.

This isn't a Controller Manager limitation — it's a Forza engine constraint
that no amount of clever reveal timing can fix. But CM makes the
consolidated setup trivial: hide all the raw peripherals, expose only the
wheel + vJoy, done.

### ② "I run SimHub / Pit House / G HUB and I don't want to break them"

You don't have to do anything. Controller Manager uses HidHide's *inverse whitelist*
mode during a session — only the game's exe is denied; every other process
(including all your companion software) keeps full device access. There's no list
to maintain.

This works automatically for any companion app, dashboard, telemetry tool, or
config utility — they don't need to be running when you set up the profile.

### ③ "My controller game keeps grabbing my wheel instead of my gamepad"

Hide everything except the gamepad for that one game.

1. **Games tab → New profile** — name it after the game, browse to its `.exe`.
2. Add the gamepad → **Always Visible**.
3. Don't add any other devices. *(Anything not in the profile is hidden from the
   game by default.)*
4. **Save Profile**.

That's the whole profile. No reveal step, no timing — the gamepad is just the only
thing the game sees, start to finish.

### ④ "I stream games to my couch / phone with Sunshine or Apollo"

The remote client's input shows up on the host as a *virtual gamepad* (created by
ViGEm). If the host has physical controllers plugged in, the game might assign
them slots ahead of the virtual one — and your remote controls do nothing.

1. **Start a remote session first** so the virtual gamepad is present in the
   device picker.
2. **Games tab → New profile** — name it after the game.
3. Add the **virtual gamepad** → **Always Visible**.
4. Don't add anything else. *(Physical controllers stay hidden by default.)*
5. **Save Profile**.

Then in Sunshine: **Configuration → Applications → Edit your game**.

- **Command Preparations → cmd (blocking)** = `"C:\path\to\ControllerManager.exe" --launch <profileId>`
- **Detach Command** = same, or rely on the process watcher.

The easiest way to get the right `--launch <profileId>` command is to use the
**Desktop** or **Start Menu** shortcut button in the Games tab — the exported
`.lnk` has the full command line inside it.

---

## How to launch a profile

Once you've got a profile saved, three ways to fire it:

| Method | When to use it | UAC prompt? |
|---|---|---|
| **In-app Launch button** (Dashboard) | Testing, manual sessions | No (CM is already running) |
| **Desktop / Start Menu shortcut** | Most users — set it once, double-click forever | No (uses a scheduled task) |
| **Auto-trigger when game launches** (profile setting) | Set-and-forget — works for any launch method | No, but a small race window |

The **Desktop / Start Menu shortcuts** are the recommended path for most games.
Click **Desktop** or **Start Menu** in the Games tab to generate one — the
shortcut targets the game's icon, looks like a normal game shortcut, and is
UAC-free thanks to a per-profile scheduled task.

**Auto-trigger** lives on each profile as a checkbox: **"Auto-trigger this
profile when the game launches"**. With it on, Controller Manager polls
every 500ms for the profile's `.exe` and applies the profile automatically.
Catches the game fast but has a narrow race window (~0-500ms) where the
game might already be enumerating controllers. Slow-starting games like
Forza Horizon have 10+ seconds of warmup before they scan, so the watcher
wins that race comfortably. For games that scan immediately at startup,
prefer the shortcut or in-app Launch path.

> **Heads-up:** Two profiles can't auto-trigger on the same `.exe`. If you
> tick the checkbox and another profile is already auto-triggering on the
> same game, Controller Manager refuses to enable it and tells you which
> profile already owns that exe — turn it off on the other profile (or
> point one of them at a different exe) first.

---

## The Devices tab

A live list of every HID gaming device on your system, plus an **Input Monitor**
expander at the bottom.

- **ON/OFF toggle** per device — same effect as a HidHide global hide. Turn off any
  device you never want games to see (e.g. a noisy DS4 you only use for SimHub).
  Game profiles override this on a per-game basis.
- **Show all devices** — uncheck to see only game-class HIDs; check to see keyboards,
  mice, audio devices, etc. (Useful for diagnosing.)
- **Input Monitor** — pick a device, expand the panel, and you'll see live axis
  bars, button lights, and joystick pads for any X/Y stick pairs. Each axis and
  joystick has a **Calibrate** button that measures idle drift over 5 seconds and
  recommends a deadzone value you can plug into your game's settings.

---

## Identifying your devices

Sim racing hardware often shows up with generic Windows names like "HID-compliant
game controller." The VID:PID is the give-away:

| Brand | VID |
|---|---|
| MOZA Racing | `VID_346E` |
| SIMAGIC | `VID_3670` |
| Fanatec | `VID_0EB7` |
| Heusinkveld | `VID_30B7` |
| Simucube / Granite Devices | `VID_16D0` |
| Thrustmaster | `VID_044F` |
| Logitech | `VID_046D` |
| Asetek SimSports | `VID_2433` |
| Cammus | `VID_3416` |
| STM32-based DIY (FreeJoy / Cube Controls / VRS) | `VID_0483` |

VID:PID shows up as a tooltip when you hover a device name in the Devices tab.
Right-click a row → **Copy VID:PID**, **Copy instance ID** for paste-able strings.

Can't tell two identical devices apart? Unplug one, hit **Refresh**, see what
disappears.

---

## The honest limit

The reveal-after-start phase relies on the game supporting **hot-plug device
detection**. Modern games using WGI, RawInput, or current XInput all handle this
fine — they notice when a new controller appears and integrate it. **Pure legacy
DirectInput-only games that scan controllers once at startup and never again**
won't see your pedals if they're hidden at that moment.

For those games, you have to choose:
- **FFB on the wheel + no pedals** (hide everything except wheel; don't reveal),
- **OR all devices visible at launch + wrong slot assignment.**

In practice this is rare — FH5/FH6, all sim racing titles I'm aware of, and every
mainstream PC game in the last decade use hot-plug-aware input. If you hit a game
where reveal doesn't work, file an issue with the game name and I'll add it to a
known-broken list.

---

## Troubleshooting

**"No force feedback" after launching with Controller Manager**
- Make sure you launched the game *through* the profile (Dashboard button, exported
  shortcut, or auto-triggered by process watcher). Launching the game directly
  won't apply the profile.
- For FH6 / Forza Motorsport specifically: confirm Forza Compatibility Mode is on
  in Pit House (MOZA) or equivalent in your wheel software.
- Check that your **wheel base** is set to **Always Visible** in the profile.

**"My wheel ended up as slot #2 (or #3)"**
- If the "Wait until the game opens the first device" checkbox is on, the
  game might be committing slot #1 too slowly for the default grace period.
  Bump **Wait this many seconds after:** in the profile editor to 2.0 or
  2.5 seconds. (Per-row Reveal At times are *added on top* of this grace —
  so if you've already set them, bumping grace pushes all reveals back.)
- If the checkbox is off, your per-row **Reveal at** times are absolute
  seconds from game launch. Make sure the wheel is **Always Visible** so
  the game's startup scan finds it alone, and spread the Reveal After Start
  times out (e.g. 11.0s, 11.1s, 11.2s) so they arrive after the slot #1
  assignment.

**"The game doesn't see my pedals after they're supposed to reveal"**
- The game might not support hot-plug detection (see [Honest limit](#the-honest-limit)).
- Verify the per-device **Reveal at** times — if you set everything to 0s
  they all appear at once and the game's scan sees them all together,
  defeating the slot-#1 assignment.
- The game might have a hard detection cutoff a few seconds after first
  scanning. Tighten the reveal spread (e.g. all devices within 1s of each
  other) so nothing falls past the cutoff. The "Wait until first device"
  checkbox handles this automatically since reveals fire back-to-back once
  the signal lands.

**"Some devices weren't hidden — the game saw them anyway"**
- Make sure the device appears in the Games-tab device picker (in the row
  list, before you add it to the profile). If it shows up in the picker only
  with **Show all devices** checked, it has no inputs — the app skips those
  during a session. If it has inputs and was still seen by the game,
  double-check it's not assigned a role in the profile — anything in the
  profile is visible to the game in some form. To hide it, just remove it
  from the profile entirely.

**Devices show as off in the Devices tab after I closed the app mid-session**
- HidHide retains the persistent blacklist across reboots; if Controller Manager
  crashed during a session, some session devices are stuck in the global hide
  list. Toggle them back **ON** in the Devices tab.

**The "Inputs Monitor" doesn't show any axes / buttons**
- Select a device in the list before expanding the monitor. Some companion apps
  hold devices exclusively — close them temporarily to test.

**A device shows up labeled "HID-compliant game controller" with no real name**
- The Windows driver hasn't registered a friendly name for it. The VID:PID tooltip
  still works — use that to identify it. (PRs welcome to expand the brand table
  in this README.)

**Filing an issue / collecting diagnostic logs**
- In **Settings → Logging**, set the level to **Verbose** (or **Debug** for
  HID-class details if I ask for it). Reproduce the issue, then attach
  `%LOCALAPPDATA%\ControllerManager\log.txt` to your issue. Debug-level logs
  include per-device kernel HIDCLASS events which are firehose-chatty — only
  use that level for short test sessions.

---

## Contributing

PRs welcome for:
- Bug fixes and improvements
- Brand additions to the VID table above
- Game-specific notes (especially anything you confirm works or doesn't work)

GitHub: [Ginger-Beard/ControllerManager](https://github.com/Ginger-Beard/ControllerManager)

---

## Project layout

```
/app                 — WPF app (.NET 10, MVVM)
  /Services          — HidHide client, device enumeration, orchestrator
  /ViewModels        — Dashboard, Games, Devices, Settings, Input Monitor
  /Views             — XAML
  /Models            — Profile, HidDevice, AppSettings
/HidHide             — Reference HidHide source (forked from nefarius/HidHide)
/tools/DeviceWatcher — Companion CLI for testing hide/show behavior
```

---

## Credits

Controller Manager is built on top of **[HidHide](https://github.com/nefarius/HidHide)**
by [Nefarius](https://github.com/nefarius) and the many other contributors —
a signed, MIT-licensed kernel filter driver that does the actual device hiding.
Without their work none of this would be possible. Huge thanks for the tireless
work maintaining and improving it.

- Maintained by [Ginger-Beard](https://github.com/Ginger-Beard).
- VID data curated for sim racing hardware; upstream USB ID database at
  [usbutils](https://github.com/gregkh/usbutils).
