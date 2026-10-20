# On Air

**Your camera or microphone turns on, the "On Air" sign at the door lights up.** No buttons, no
manual switching: a small Windows app watches the same signal Windows uses for its own privacy
indicator and sends it over Bluetooth LE to a battery-powered ESP32-C3 sign with eight red LEDs.

[Türkçe](README.tr.md) · [Windows app details](windows/README.md) · [Download](https://github.com/Guidin9/onair-ble/releases/latest)

![3D-printed On Air enclosure](docs/images/enclosure.png)

The sign was built to warn anyone about to walk in that a meeting or video call is going on. The
whole thing is in this repository: the Windows companion app, the device firmware, an LED bring-up sketch
and the 3D-printable enclosure.

## How it works

```
 Windows PC                                            On Air sign (ESP32-C3)
┌──────────────────────────────────────┐              ┌──────────────────────────────┐
│ Camera / mic consent store (HKCU)    │              │ GATT "state" characteristic  │
│   LastUsedTimeStop == 0  → in use    │              │   encrypted + bonded writes  │
│            │ RegNotifyChangeKeyValue │              │            │                 │
│            ▼                         │  Bluetooth   │            ▼                 │
│ OnAirIndicator.exe (tray app)        │ ───────────▶ │ 8 red LEDs breathe while     │
│   state byte: bit0 cam, bit1 mic     │  LE, 1 byte  │ camera or mic is on          │
│   heartbeat every 5 min              │              │ 30 min failsafe turns it off │
└──────────────────────────────────────┘              └──────────────────────────────┘
```

1. Windows records, per app, when the webcam and microphone were last started and stopped. A
   `LastUsedTimeStop` of `0` anywhere under the consent store means the device is in use right now.
   The app is notified of changes by the registry itself, so it uses ~0% CPU while idle.
2. Changes are debounced for 500 ms and turned into one byte: bit 0 = camera, bit 1 = microphone.
3. The byte is written to the sign over an **encrypted, bonded** BLE link. The sign rejects writes
   from anything it hasn't paired with.
4. The sign breathes its LEDs while the byte is non-zero. If the PC disappears (crash, sleep, out of
   range) without clearing it, a 30-minute failsafe turns the light off. The app re-sends the state
   every 5 minutes so a long call never hits that limit.

## What's in the repo

| Path | What it is |
|---|---|
| [`windows/`](windows/) | Windows companion app: .NET 10, WinForms tray icon, WinRT Bluetooth. No NuGet packages. Installs and uninstalls itself without admin rights. |
| [`firmware/OnAir_BLE_C3/`](firmware/OnAir_BLE_C3/) | Device firmware for the ESP32-C3 SuperMini (Arduino, NimBLE). |
| [`firmware/LED_Breathing_Test/`](firmware/LED_Breathing_Test/) | Hardware bring-up sketch: breathes the LEDs with no BLE, to find wiring mistakes first. |
| [`hardware/enclosure/`](hardware/enclosure/) | 3D-printable enclosure (STL). GitHub shows a 3D preview when you open a file. |
| [`windows/docs/KURULUM-WINDOWS.md`](windows/docs/KURULUM-WINDOWS.md) | Install guide in Turkish for whoever receives the EXE, including SmartScreen and Smart App Control warnings. |

## Hardware

### Parts

| Part | Qty | Notes |
|---|---|---|
| ESP32-C3 SuperMini | 1 | BLE 5.0, RISC-V. Any ESP32-C3 board works if you adjust the pins. |
| Red LED | 8 | Two per GPIO. |
| 330 Ω resistor | 8 | **One per LED.** Parallel LEDs sharing one resistor drift apart and the brighter one burns out first. |
| 100 kΩ resistor | 2 | Battery voltage divider. |
| 100 nF capacitor | 1 | GPIO3 to GND. Without it the ADC reads low from the 50 kΩ source. |
| 1S Li-ion battery | 1 | 3.4–4.2 V. The power figures below assume 3000 mAh. |
| Power switch | 1 | The divider sits on the switched battery rail. |

### Wiring

| ESP32-C3 pin | Connection |
|---|---|
| GPIO5 | 330 Ω → red LED, ×2 → GND |
| GPIO6 | 330 Ω → red LED, ×2 → GND |
| GPIO7 | 330 Ω → red LED, ×2 → GND |
| GPIO10 | 330 Ω → red LED, ×2 → GND |
| GPIO3 | Battery sense: 100 kΩ / 100 kΩ divider from the switched battery rail, 100 nF to GND |
| GPIO9 | BOOT button (active low). Hold 3 s to forget all paired PCs. |

LEDs are active-high: long leg (anode) towards the GPIO. Avoid GPIO2 and GPIO8 (strapping pins),
GPIO20/21 (UART0) and GPIO18/19 (native USB).

### Enclosure

![Enclosure parts](docs/images/enclosure-parts.png)

| File | Size (mm) | Purpose |
|---|---|---|
| [`onair_box_body_border_midpoint.stl`](hardware/enclosure/onair_box_body_border_midpoint.stl) | 134 × 85 × 49 | Main box; its face carries the "ON AIR" lettering inside a border. |
| [`onair_backplate.stl`](hardware/enclosure/onair_backplate.stl) | 131 × 81 × 2 | Plate with "ON AIR" cut all the way through. |
| [`onair_backplate_mirrored.stl`](hardware/enclosure/onair_backplate_mirrored.stl) | 131 × 81 × 2 | Mirror image of the same plate. |
| [`onair_separator_bar.stl`](hardware/enclosure/onair_separator_bar.stl) | 35 × 80 × 5 | Divider that runs across the middle of the box. |
| [`onair_logo_emboss.stl`](hardware/enclosure/onair_logo_emboss.stl) | 20 × 22 × 1.3 | Logo relief for the side of the box. It is a separate body, so it can be printed in another colour. |

The box, the divider and the logo share one coordinate system, so importing them together into a
slicer puts the divider and the logo in place. [`hardware/enclosure/v1/`](hardware/enclosure/v1/)
holds the first revision with a 39 mm deep box.

## Firmware

### Build and flash

1. Arduino IDE 2 with the **ESP32 Arduino core 3.x** (the firmware uses `ledcAttach`, which core
   2.x doesn't have).
2. Library: **NimBLE-Arduino 2.x**.
3. Board: *ESP32C3 Dev Module*, **USB CDC On Boot: Enabled**.
4. Flash [`LED_Breathing_Test`](firmware/LED_Breathing_Test/) first. All eight LEDs should fade up
   and down together every 3 seconds. `TEST_MODE 1` breathes the pins one by one to find a miswired
   one.
5. Flash [`OnAir_BLE_C3`](firmware/OnAir_BLE_C3/). Keep `DEBUG_SERIAL` on during setup to see the
   MAC address and `[BLE]` messages at 115200 baud, and comment it out for daily use.

> **Updating without losing the pairing:** pairings live in NVS. Keep *Tools → Erase All Flash
> Before Sketch Upload* **Disabled** and an update leaves them in place.

### What the LEDs mean

| State | LEDs |
|---|---|
| Just powered on, or the PC disconnected | Two short, dim pulses every 3 s for 60 s, then dark |
| Connected, camera and mic off | Dark. The CPU drops into light sleep. |
| Camera or microphone on | All eight breathe (gamma-corrected, 3 s period) |
| BOOT held for 3 s | Acknowledgement blink, all pairings erased |

Everything else stays dark on purpose: a red light at the door has to mean only one thing.

### Bluetooth contract

| | UUID | Properties |
|---|---|---|
| On Air service | `6f2a1b40-9c3e-4d17-8a52-b1c7e0f43d91` | |
| State characteristic | `6f2a1b41-9c3e-4d17-8a52-b1c7e0f43d91` | Read/write (encrypted), notify. One byte: bit 0 camera, bit 1 microphone. |
| Battery service | `0x180F` (standard) | |
| Battery level | `0x2A19` (standard) | Read, notify. Percentage, read every 5 minutes. |

Pairing is Just Works with bonding, so there is no PIN prompt. The device advertises only while
nothing is connected, and only the service UUID goes in the advertisement; the name "On Air" is in
the scan response. TX power is +3 dBm, which is enough for a desk device one or two metres away.

### Power

The CPU, not the LEDs, is the big consumer: eight LEDs at 330 Ω peak around 34 mA and average about
13 mA over the breathing curve. Battery life therefore depends on whether light sleep is available.

| Build | Idle | Breathing | Per day (22 h idle, 2 h on air) | 3000 mAh lasts |
|---|---|---|---|---|
| Stock Arduino core (no power management), CPU at 80 MHz | ~20 mA | ~33 mA | ~505 mAh | ~6 days |
| Core built with `CONFIG_PM_ENABLE` (custom sdkconfig, PlatformIO or ESP-IDF) | ~2 mA | ~33 mA | ~110 mAh | ~4 weeks |

These are estimates from the firmware's power budget, not long-term measurements. The stock Arduino
core ships with power management off; the firmware detects that, logs it once and keeps working.
Desoldering the board's power LED saves another 1–3 mA.

## Windows app

Download `OnAirIndicator.exe` from the [latest release](https://github.com/Guidin9/onair-ble/releases/latest)
and run it. A small window offers **Install**, which copies the app to `%LOCALAPPDATA%\OnAir`,
starts it at sign-in and pairs with the sign on its own. No admin rights are needed, and
**Uninstall** removes everything again, including the Bluetooth pairing.

| Command | What it does |
|---|---|
| `OnAirIndicator.exe` | Opens the Install / Uninstall window |
| `OnAirIndicator.exe --install` | Installs without the window |
| `OnAirIndicator.exe --uninstall` | Removes the app, the autostart entry and the pairing |

The tray icon shows the connection, the battery level and the camera/mic state, and has
**Re-pair device** and **Reconnect now** for when something goes wrong. Logs are kept for 7 days in
`%LOCALAPPDATA%\OnAir\logs`.

The release EXE carries a self-signed certificate, so Windows may show a SmartScreen warning the
first time: choose *More info → Run anyway*, or run `Unblock-File .\OnAirIndicator.exe`. Details,
including the rarer Smart App Control block, are in [`windows/README.md`](windows/README.md#troubleshooting).

### Build it yourself

Requires the .NET 10 SDK on Windows 10 2004 or later.

```powershell
cd windows
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The result is a single ~55 MB EXE in `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\`.
Almost all of it is the .NET runtime; the app itself is about 0.2 MB.

Architecture notes, reconnect rules and the threading model are in [`CLAUDE.md`](CLAUDE.md) and
[`windows/README.md`](windows/README.md).

## License

[MIT](LICENSE) © 2026 Mert Demirdöven
