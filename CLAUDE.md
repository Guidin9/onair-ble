# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Windowless Windows app (.NET 10, WinForms + WinRT) that detects camera/microphone use and mirrors it
over Bluetooth LE to an ESP32-C3 "On Air" light running the `OnAir_BLE_C3.ino` firmware. Single
self-contained EXE that installs, runs, and uninstalls itself with no admin rights.

Repository layout:

- `windows/` — the .NET app (all paths in the Build and Architecture sections below are relative to it)
- `firmware/OnAir_BLE_C3/` — device firmware (Arduino, NimBLE-Arduino 2.x, ESP32 core 3.x)
- `firmware/LED_Breathing_Test/` — LED wiring bring-up sketch, no BLE
- `hardware/enclosure/` — 3D-printable STL parts (`v1/` is the first revision)

## Build

Run from `windows/`.

```powershell
dotnet build -c Release

# single-file publish (note: the csproj sets SelfContained=false; the RID and
# self-contained flag are supplied on the command line)
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output EXE is `OnAirIndicator.exe` (the `AssemblyName`, deliberately different from the project name
`OnAirBLe`). Requires the .NET 10 SDK; the `net10.0-windows10.0.19041.0` TFM is what projects the
WinRT Bluetooth APIs — there are **no NuGet packages, by design**, and new dependencies should be
avoided.

There is no test project and no linter configured.

### Running locally

`dotnet run` (no args) opens the Install/Uninstall/Cancel window. To exercise the actual app logic
without installing, run the built EXE with `--run`. Note that `--run` uses a global single-instance
mutex (`Global\OnAirIndicator_SingleInstance`), so an already-installed copy must be exited from its
tray icon first. Runtime output goes to `%LOCALAPPDATA%\OnAir\logs\onair-YYYY-MM-DD.log`; set
`"logLevel": "debug"` in `%LOCALAPPDATA%\OnAir\config.json` for per-write/scan detail.

## Architecture

One EXE with four roles, dispatched in `Program.cs` by argv[0]: `--install`, `--uninstall`, `--run`,
or (no args) the setup window. `--run` is what the `HKCU …\Run` autostart entry invokes.

**This is a user-session app, not a Windows Service, and must stay that way.** WinRT Bluetooth is
unreliable in Session 0, and the CapabilityAccessManager consent keys it reads live under `HKCU`.
That constraint is also why autostart is the HKCU Run key and why everything installs under
`%LOCALAPPDATA%\OnAir` — no admin rights anywhere.

### Data flow

```
RegistryWatcher (RegNotifyChangeKeyValue, subtree)
  → CameraMicMonitor (debounce 500 ms, compute state byte)
    → OnAirAppContext.OnCameraMicStateChanged
      → BleController.SetDesiredState(byte)   → GATT write
      → TrayIcon.UpdateState                  (marshalled to UI thread)
```

- **`CameraMicMonitor`** scans the consent store subtrees (HKCU + HKLM, webcam + microphone) for any
  `LastUsedTimeStop == 0`, which means "in use right now". Event-driven via `RegistryWatcher`; falls
  back to 2 s polling if no watcher could be armed, plus a 30 s safety re-read.
- **`RegistryWatcher`** wraps the one-shot `RegNotifyChangeKeyValue`: it must re-arm after every
  notification, which `OnEventSignalled` does.
- **`BleController`** owns a single background maintenance loop: pick a target address →
  JustWorks/`ConfirmOnly` pairing → `GattSession.MaintainConnection = true` → wait for `Connected` →
  resolve GATT **uncached** → write state → block until disconnect, then exponential backoff
  (2 s → 60 s).
- **`BleDevicePresence`** is the "device is here" signal source, deliberately **outside** the
  maintenance loop so it survives across connect attempts. See below.
- **`OnAirAppContext`** is the `ApplicationContext` that owns everything and hosts the message pump
  (needed for both the tray icon and `SystemEvents`). It runs even when `showTrayIcon` is false.

### Connecting is MAC-first, not scan-first — do not regress this

The single-host firmware **stops advertising the moment a host attaches**, and Windows auto-connects
to the bonded device on its own (bond + `MaintainConnection`). So after the device is power-cycled it
can be "Connected" in Windows settings and completely silent on the air. Any code path that waits for
an advertisement before connecting deadlocks forever in that state — this was a real bug.

The rules that avoid it:

- If `config.DeviceMac` is set, connect **straight** to `BluetoothLEDevice.FromBluetoothAddressAsync`.
  Never gate that on a scan.
- Advertisement discovery is only the first-time path (nothing paired yet) and the fallback after
  `DirectConnectFailureThreshold` consecutive direct failures.
- `BleDevicePresence` merges two sources: the advertisement watcher (finds unpaired devices; runs
  whenever the link is down, paused while connected since the device is silent then) and a
  `DeviceWatcher` over paired BLE endpoints watching `System.Devices.Aep.IsConnected` (the only thing
  that catches an OS-initiated connection). The `DeviceWatcher` source needs
  `SetExpectedAddress` to be able to tell our device from any other paired peripheral.
- Backoff sleeps go through `WaitForSignalOrDelayAsync`, not `Task.Delay` — the sleep is cut short
  the instant the device reappears, so a grown 60 s backoff can't stall a reconnect.

### Reconnect hygiene

Three things must hold on every reconnect, or writes fail silently after a device restart:

- **Nothing is reused.** `TeardownConnectionAsync` disposes in order — detach event handlers, null
  the characteristics under `_writeLock`, dispose `GattDeviceService`s, then `GattSession`, then
  `BluetoothLEDevice` — and every field is nulled. Retained `GattDeviceService` objects in particular
  hold OS-side handles open; they must be kept in fields precisely so they can be disposed.
- **GATT is always `BluetoothCacheMode.Uncached`.** Device restarts change attribute handles.
- **Bounded time.** The connect phase (`EstablishAsync`) runs under a 30 s `CancelAfter`, with WinRT
  calls awaited via `.AsTask(ct)` so a hung call can actually be cancelled. `WaitForDisconnectAsync`
  is deliberately outside that bound.

Two watchdogs back this up: `WriteFailureThreshold` consecutive failed writes while `State ==
Connected` cancels the cycle (stale link), and the connect-phase timeout cancels a wedged attempt.

### Firmware contract — keep in sync with `firmware/OnAir_BLE_C3/OnAir_BLE_C3.ino`

Changing either side alone breaks the link silently:

- Service UUID `6f2a1b40-9c3e-4d17-8a52-b1c7e0f43d91`, state characteristic `…41…`
  (`BleController.ServiceGuid` / `StateCharGuid`). Discovery filters advertisements by the service
  UUID, so a mismatch presents as "device never found".
- State byte: bit0 = camera, bit1 = microphone (`CameraMicMonitor.StateBitCamera` /
  `StateBitMicrophone`).
- The firmware has a 30-minute failsafe that clears the light if it stops receiving writes; the
  heartbeat (default 300 s, floor 30 s) exists solely to stay under it. Don't raise the default near
  30 min.
- The state characteristic requires an **encrypted, bonded** link — a `ProtocolError` on write means
  the bond was lost, not a transient failure, which is why that path notifies the user to re-pair
  (once per connection, not per failed write).
- The device only advertises while **unconnected**. Treat "not seen in a scan" as "no information",
  never as "not there" — see the MAC-first section above.

### Threading model

The maintenance loop and all WinRT calls run off the UI thread. Rules the code relies on:

- Every callback that touches `TrayIcon` goes through `OnAirAppContext.Post`, which marshals via a
  hidden `Control`. `TrayIcon` members are UI-thread-only.
- GATT writes are serialized by `_writeLock` so a state write and a heartbeat write can never
  overlap; `_stateChar` is re-read under that lock because teardown nulls it.
- `_stateGate` guards the desired-state byte and the failure counters. `_directConnectFailures` is
  the exception — it is touched only from the single maintenance task.
- Cancellation is two-level: `_cts` ends the app, `_cycleCts` cancels only the current connect
  cycle. "Reconnect now", "Re-pair" and the stale-link watchdog all work by cancelling the cycle,
  not by tearing down the loop. The connect-phase timeout is a third, nested CTS — the
  `when (!ct.IsCancellationRequested)` guard in `ConnectAndServeAsync` is what tells a timeout apart
  from a user-requested cancellation.
- `BleDevicePresence` has its own lock; its events arrive on thread-pool threads and only complete a
  `TaskCompletionSource` — it never touches the UI thread.

### Logging

The log is the only diagnostic channel for a windowless app, so **`WriteStateAsync` logs every
attempt including the skipped ones**. Detecting a state change and actually writing it to the device
are different events; a log that shows `Camera/mic state = 0x01` with no corresponding write line is
the exact ambiguity that made the reconnect bug hard to diagnose. Successful state writes are Info,
heartbeat writes Debug, anything else Warn with the `GattCommunicationStatus` and protocol error
code.

### Error-handling conventions

Failures are logged and swallowed at nearly every layer — the app must keep running and must never
show an unhandled crash. `Config.Load` never throws (bad JSON → defaults), logging is best-effort,
and `Program.InstallGlobalExceptionHandlers` is the final net (it marks unobserved task exceptions
observed so the process survives). Only genuinely user-actionable conditions (pairing failed, write
rejected, stale GATT, device missing for 3 cycles) surface as tray balloons via
`BleController.UserNotification`.

### Install/uninstall specifics

`Installer` kills other `OnAirIndicator` processes before touching the EXE. Uninstall also unpairs
the BLE device using the saved MAC, and — when the running process *is* the installed copy — cannot
delete itself, so it schedules `MoveFileEx(…, DELAY_UNTIL_REBOOT)` plus a detached `cmd` that waits
and removes the tree. Uninstall intentionally leaves the EXE the user launched from in place, and
install preserves an existing `config.json`.
