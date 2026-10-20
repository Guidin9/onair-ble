# On Air — Windows BLE Companion

A tiny, windowless Windows app that detects when your **camera or microphone** is in use and
mirrors that status over **Bluetooth LE** to an ESP32‑C3 "On Air" light. It ships as a single
self‑contained `.exe` that installs, runs, and uninstalls itself — **no admin rights required**.

The device firmware is [`OnAir_BLE_C3.ino`](../firmware/OnAir_BLE_C3/OnAir_BLE_C3.ino) (ESP32‑C3, NimBLE); the hardware, wiring and enclosure are described in the [main README](../README.md). This app is written to that firmware's
GATT contract exactly.

---

## What it does

- Watches the Windows **CapabilityAccessManager consent store** (event‑driven, ~0% idle CPU) to know
  when the webcam/mic are active — the same source Windows uses for its own privacy indicator.
- Connects to the device by its custom service UUID, performs a one‑time **JustWorks pairing**, and
  keeps an **encrypted, bonded** GATT link (Windows auto‑reconnects when the device is in range).
- After the first pairing it reconnects **directly to the saved MAC**, without waiting for the device
  to advertise — the device goes silent as soon as Windows attaches to it, so waiting for an
  advertisement would never succeed. It also watches Windows' own "paired device is connected"
  signal, so an OS‑initiated connection is picked up straight away.
- Writes a 1‑byte state to the device: **bit0 = camera, bit1 = microphone**.
- Re‑writes the current state every 5 minutes (**heartbeat**) so the firmware's 30‑minute failsafe
  never turns the light off while you're still in a call.
- Starts automatically at sign‑in, shows a **system‑tray** status icon, and raises balloon
  notifications on critical errors.

### Why it is not a Windows Service (by design)

WinRT Bluetooth is unreliable in Session 0, and the consent‑store keys live under `HKCU` for the
signed‑in user — which a Session‑0 service can't read. So this is a normal, windowless **user‑session**
app, auto‑started from the `HKCU …\Run` key.

---

## Build

Requires the **.NET 10 SDK** (target framework `net10.0-windows10.0.19041.0`). No NuGet packages —
everything (WinRT BLE, WinForms, registry, JSON) comes with the framework.

```powershell
dotnet build -c Release
```

### Produce the single self‑contained EXE

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\OnAirIndicator.exe` — copy this one
file anywhere. (On `cmd.exe`, replace the backtick line‑continuations with `^`.)

The EXE is **~55 MB**. Almost none of that is this app (~0.2 MB) — it is the .NET runtime, WinForms
and the WinRT projections that self‑contained deployment has to carry. `EnableCompressionInSingleFile`
in the `.csproj` deflates the bundle (it would be ~135 MB otherwise); the only cost is decompressing
assemblies at each start. Trimming is deliberately not enabled — WinForms is unsupported for
trimming and the WinRT projections rely on reflection.

---

## Install / run / uninstall

| Command | What it does |
|---|---|
| `OnAirIndicator.exe` | Opens a small **Install / Uninstall / Cancel** window. |
| `OnAirIndicator.exe --install` | Copies itself to `%LOCALAPPDATA%\OnAir`, adds the autostart entry, writes a default `config.json`, and starts running. |
| `OnAirIndicator.exe --uninstall` | Stops the app, removes autostart, **unpairs the device**, deletes `%LOCALAPPDATA%\OnAir`. Leaves your original `.exe` in place. |
| `OnAirIndicator.exe --run` | Runs the windowless background app (this is what autostart invokes). |

> Uninstall never deletes the EXE you launched it from — keep it if you want to reinstall later.

Everything the app creates lives under **`%LOCALAPPDATA%\OnAir\`**:

```
OnAirIndicator.exe     the installed copy (what autostart runs)
config.json            settings (see below)
logs\onair-YYYY-MM-DD.log   daily logs, last 7 days kept
```

---

## First‑time pairing

1. Power on the device and make sure it is **advertising** (its LED logic / serial log shows it's not
   already connected to another PC). Keep `DEBUG_SERIAL` enabled in the firmware to read its **MAC**
   and `[BLE]` messages during setup.
2. Run `OnAirIndicator.exe --install` (or Install from the window).
3. The app discovers the device by its service UUID and pairs automatically — **JustWorks bonding has
   no prompt**. On success it saves the device MAC to `config.json` and connects.
4. Turn your camera on: the device LED should light within a fraction of a second; turn it off and the
   LED goes out. The state byte the app writes is echoed back over notify and appears in the firmware
   serial log.

You can watch progress in the tray menu (**Status**) or in the log file (**Open logs**).

---

## Configuration — `config.json`

```json
{
  "deviceMac": "AA:BB:CC:DD:EE:FF",
  "heartbeatSeconds": 300,
  "showTrayIcon": true,
  "logLevel": "info"
}
```

| Key | Meaning |
|---|---|
| `deviceMac` | Filled in automatically after the first pairing. Clear it to force a fresh discovery. |
| `heartbeatSeconds` | How often the current state is re‑written (min 30, default 300). Keep it well under the firmware's 30‑minute failsafe. |
| `showTrayIcon` | `false` hides the tray icon (the app still runs). |
| `logLevel` | `error`, `warn`, `info`, or `debug`. |

Edit it via the tray's **Open config.json**, then restart the app (tray **Exit**, or sign out/in).

---

## Tray menu

- **Status** — connection state, device MAC, battery %, and current camera/mic state.
- **Re‑pair device** — unpairs and rediscovers. Use after a firmware update or if writes start failing.
- **Reconnect now** — forces an immediate reconnect instead of waiting out the backoff.
- **Open logs** / **Open config.json** — jump to the files.
- **Exit** — stop the background app (it starts again at next sign‑in).

---

## Troubleshooting

### The device was switched off and back on, and now it never reconnects
This is fixed — the app no longer waits for an advertisement when it already knows the device's MAC.
It should reconnect within a few seconds of the device coming back, with no restart needed. If it
doesn't, the log tells you which stage failed; look for `Device target via direct-mac`,
`Connection status changed: Connected`, `GATT resolved`, then `State write 0xNN ok`. A repeated
`Connect phase timed out` means the device is powered on but not accepting the connection — clear its
bonds (BOOT‑button hold) and Re‑pair.

### "Device not found" / never connects
- Make sure **Bluetooth is on** in Windows and the PC has BLE (Bluetooth 4.0+).
- The device only advertises while **unconnected**, so this applies to **first‑time pairing**. If it's
  still connected/bonded to **another PC**, it won't advertise here — on the device, **hold the BOOT
  button ~3 s** to clear all bonds and re‑enter pairing, then Re‑pair from the tray.
- Confirm the firmware's `ONAIR_SERVICE_UUID` matches this app's service UUID
  (`6f2a1b40-9c3e-4d17-8a52-b1c7e0f43d91`). If the firmware UUIDs were changed, discovery filters it
  out.
- Bring the device closer; check the log (`Open logs`) for `Scan timed out` vs `Device found`.

### "Write rejected" / light doesn't follow the camera
- The `state` characteristic requires an **encrypted, bonded** link. A rejected write almost always
  means the **bond was lost** (device re‑flashed, bonds cleared, or Windows forgot the device). Use
  **Re‑pair device** from the tray.
- If it persists, remove the device under **Windows Settings → Bluetooth & devices**, clear the
  device's bonds (BOOT‑button hold), then Re‑pair.

### "Device needs re‑pairing" (stale GATT)
- Windows caches the GATT table. After a firmware change that alters the services/characteristics, the
  cached layout can be wrong and the state characteristic won't resolve. **Remove the device in
  Windows Bluetooth settings and re‑pair** (the app already reads GATT uncached, but the OS cache can
  still need a reset).

### Light stays on after a crash / sleep
- The firmware's **30‑minute failsafe** turns the light off if it stops receiving writes. The app also
  writes `0` when the session **locks**. On resume from sleep it proactively reconnects.

### Nothing appears to happen
- Check `%LOCALAPPDATA%\OnAir\logs\`. Set `"logLevel": "debug"` for verbose output (presence
  sightings, battery readings, heartbeat writes).
- **Every** state write is logged with its outcome, including the ones that were skipped because the
  link was down (`State write 0x01 skipped: not connected`). Detecting the camera and writing to the
  device are separate steps — a `Camera/mic state = 0x01` line on its own does **not** mean the
  device was updated; look for the matching `State write 0x01 ok (status=Success)` line.

### Windows blocked the app before it even started

Two different Windows features can block an unsigned build, and they are not the same thing:

- **SmartScreen** — "Windows protected your PC". Has a way through: *More info → Run anyway*. It is
  triggered by the Mark‑of‑the‑Web, so `Unblock-File .\OnAirIndicator.exe` removes it entirely.
- **Smart App Control (SAC)** — "Smart App Control blocked an app that might be unsafe". Has **no**
  run‑anyway button. Only exists on **clean installs** of Windows 11 22H2+, so most machines never
  see it. Confirm with:

  ```powershell
  (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
  # nothing/0 = off, 1 = on (enforcing), 2 = evaluation
  ```

  A self‑signed certificate does **not** get past SAC — this was measured, not assumed. Either wait
  (Microsoft reviews blocked files automatically; this app was cleared ~30 minutes after its first
  block) or turn SAC off in Windows Security, which has been reversible since the April 2026 update.

To see whether a launch was actually blocked rather than failing for some other reason:

```powershell
Get-WinEvent -LogName 'Microsoft-Windows-CodeIntegrity/Operational' -MaxEvents 20 |
  Where-Object Id -eq 3077 | Select-Object TimeCreated, Message
```

Full explanation, the signing script, and the guide to hand to whoever you're giving the EXE to:
**[`docs/KURULUM-WINDOWS.md`](docs/KURULUM-WINDOWS.md)** (Turkish), plus
`scripts/sign-selfsigned.ps1` (developer side) and `scripts/trust-onair-cert.ps1` (recipient side).

---

## How detection works (reference)

Under each of:

```
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone
```

every app has a subkey (classic desktop apps live under `NonPackaged`, path‑encoded with `#`) holding
`LastUsedTimeStart` / `LastUsedTimeStop` FILETIMEs. A `LastUsedTimeStop == 0` **anywhere** in the
subtree means that device is in use right now. The app watches these subtrees with
`RegNotifyChangeKeyValue` (falling back to 2‑second polling if that can't be armed) and debounces
rapid changes by ~500 ms so the light doesn't flicker.
