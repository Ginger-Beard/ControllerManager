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

Every game profile is a short list of your devices, each tagged one of three ways:

- **Always Visible** — the game sees this from the moment it launches.
  *(Your wheel base goes here. Your gamepad goes here for non-racing games.)*
- **Reveal After Start** — hidden when the game starts, then revealed at a time you
  choose (e.g. "5 seconds after launch"). Order matters — first revealed gets the
  next available controller slot.
  *(Your pedals and shifter usually go here for FFB games, so the wheel grabs slot
  #1 alone.)*
- **Always Hidden** — the game never sees this device.
  *(Your gamepad for racing games. Your sim rig for non-racing games.)*

Anything not in the profile is also hidden from the game.

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
kernel driver). After that, you're set — toggle **Start with Windows** in Settings
and it'll be in the tray every time you boot, no further prompts.

---

## Use cases

### ① "I want FFB in Forza Horizon"

The classic case.

1. **Games tab → New profile** — name it "Forza Horizon 6", browse to
   `forzahorizon6.exe` (Steam: `...steamapps\common\ForzaHorizon6\forzahorizon6.exe`).
2. Click **+ Add to profile** for each device you've got — wheel, pedals, shifter,
   handbrake, button box, gamepad.
3. Set roles:
   - Wheel base → **Always Visible**
   - Pedals → **Reveal After Start**
   - Shifter → **Reveal After Start**
   - Handbrake → **Reveal After Start**
   - Gamepad → **Always Hidden**
4. Set **Reveal trigger** to **When game opens first device**. (Recommended
   for FFB games.) Leave **Wait after first device opened** at the 1.5s
   default for now.
5. Drag the Reveal-After-Start rows into the order you want them mapped to
   in-game slots #2, #3, #4. List order = reveal order = in-game order.
6. **Save Profile**, then launch by one of the methods below.

When FH6 starts, only the wheel is visible. The game does its warmup, opens
the wheel file when it's ready, and Controller Manager spots that exact moment
via kernel ETW — then waits 1.5 seconds (your "post-acquisition delay") so the
game has time to lock the wheel as controller slot #1 before revealing the
rest.

**Also set per-device T+Xs values as a safety net.** Some games (especially
those using RawInput or WGI) don't open the device file directly, so the ETW
signal never fires. In that case the T+Xs values control timing. Use values
that would work in Timer mode (for Forza-style: ~11s on the first, 11.1, 11.2
on the others) — if ETW fires earlier, the reveals just happen sooner. The
T+Xs is the upper bound, not the target.

> **MOZA tip:** turn on **Forza Compatibility Mode** in Pit House so the wheel
> presents as a Fanatec (VID `0EB7`). FH6 detects Fanatec directly via its native
> SDK and routes FFB through it cleanly.

> **If wheel still doesn't get slot #1:** bump **Wait after first device opened**
> to 2.0 or 2.5 seconds. Some games take longer to commit slot assignment after
> first noticing a controller. Worst case 3s — beyond that, something else is
> going wrong and worth filing an issue.

> **If reveals never happen at all (logs show no acquisition signal):** the
> game probably uses RawInput or WGI and doesn't call CreateFile on the device
> file. The per-device T+Xs values still control timing in that case — make
> sure they're set sensibly (e.g. 11s, 11.1s, 11.2s for Forza). The acquisition
> signal is an early-fire optimization on top of the timer; the timer is the
> source of truth.

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
   - Every physical pedal set, shifter, handbrake, button box → **Always Hidden**
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

You can copy the `--launch` command from the in-app **Copy Steam Command** button
(strip the `--steam-wrap`/`%command%` parts) or pull it from the shortcut you
exported.

---

## How to launch a profile

Once you've got a profile saved, four ways to fire it:

| Method | When to use it | UAC prompt? |
|---|---|---|
| **In-app Launch button** (Dashboard) | Testing, manual sessions | No (CM is already running) |
| **Desktop / Start Menu shortcut** | Most users — set it once, double-click forever | No (uses a scheduled task) |
| **Steam Launch Options** | Steam-launched games where you want playtime tracked | Currently yes — see below |
| **Process Watcher** (Settings) | Set-and-forget — works for any launch method | No, but a small race window |

The **Desktop / Start Menu shortcuts** are the recommended path for most games.
Click **Desktop** or **Start Menu** in the Games tab to generate one — the
shortcut targets the game's icon, looks like a normal game shortcut, and is
UAC-free thanks to a per-profile scheduled task.

The **Process Watcher** polls every 500ms for known game executables and triggers
the profile automatically when one starts. It catches the game very fast but has a
narrow race window (~0-500ms) where the game might already be enumerating
controllers. Slow-starting games like Forza Horizon have 10+ seconds of warmup
before they scan for controllers, so the watcher wins that race comfortably. For
games that scan immediately at startup, prefer the shortcut or in-app Launch path.

> **Steam Launch Options note:** the `--steam-wrap` path still triggers UAC on
> every launch because Steam needs to pass arguments through to the wrapper.
> A fix is planned (split into a non-admin launcher exe) but isn't shipped yet.
> If this matters to you, use a Desktop shortcut and launch from there — Steam
> playtime tracking is lost but everything else works.

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
- You're in acquisition mode but the game commits slot #1 too slowly for the
  default grace period. Bump **Wait after first device opened** in the profile
  editor to 2.0 or 2.5 seconds.
- If you're in Timer mode, your wheel arrived too close in time to other
  devices. Spread them out (e.g. wheel always-visible, others starting at T+11s
  or later) and confirm the wheel is set to **Always Visible** so the game's
  scan finds it alone.

**"The game doesn't see my pedals after they're supposed to reveal"**
- The game might not support hot-plug detection (see [Honest limit](#the-honest-limit)).
- In Timer mode, verify the per-device reveal times — if you set everything
  to T+0s they all appear at once and the game's scan sees them all together,
  defeating the slot-#1 assignment.
- The game might have a hard detection cutoff a few seconds after first
  scanning. Tighten the reveal spread (e.g. all devices within 1s of each
  other) so nothing falls past the cutoff. Acquisition mode handles this
  automatically since reveals fire back-to-back.

**"Some devices weren't hidden — the game saw them anyway"**
- Make sure the device appears in the Games-tab device picker (in the row
  list, before you add it to the profile). If it shows up in the picker only
  with **Show all devices** checked, it has no inputs — the orchestrator
  skips those. If it has inputs, add it to the profile with role **Always
  Hidden** or just leave it unassigned (unassigned = hidden during session).

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

---

## Contributing

PRs welcome for:
- Bug fixes and improvements
- Brand additions to the VID table above
- Game-specific notes (especially anything you confirm works or doesn't work)

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

- Hiding is done by [HidHide](https://github.com/nefarius/HidHide) by
  [Nefarius Software Solutions](https://nefarius.at/) — MIT licensed, signed
  kernel filter driver. None of this works without their work.
- VID data curated for sim racing hardware; upstream USB ID database at
  [usbutils](https://github.com/gregkh/usbutils).
