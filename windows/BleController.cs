using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace OnAirBle;

/// <summary>High-level connection state, surfaced to the tray UI.</summary>
internal enum BleConnectionState
{
    Disconnected,
    Scanning,
    Connecting,
    Connected,
}

/// <summary>
/// Owns the BLE link to the ESP32-C3 "On Air" device: discovery, JustWorks pairing/bonding,
/// a maintained GATT connection, writing the 1-byte state characteristic (with a periodic
/// heartbeat), and automatic reconnection with exponential backoff.
///
/// Connecting is MAC-first, not scan-first, and that distinction matters: the single-host
/// firmware stops advertising as soon as a host attaches, and Windows auto-connects to the
/// bonded device on its own. Waiting for an advertisement in that state deadlocks forever —
/// the device is "Connected" in Windows and silent on the air. So once a MAC is known we go
/// straight to <see cref="BluetoothLEDevice.FromBluetoothAddressAsync"/>; advertisement
/// discovery is only the first-time path and the fallback after repeated direct failures.
///
/// Device presence is tracked by <see cref="BleDevicePresence"/>, which lives outside this
/// loop and is also used to cut a backoff sleep short the moment the device reappears.
///
/// All work runs on a single background maintenance task; WinRT calls are awaited directly.
/// GATT writes are serialized with a semaphore so an on-demand write and a heartbeat write
/// can never overlap.
/// </summary>
internal sealed class BleController : IDisposable
{
    // UUIDs — must match OnAir_BLE_C3.ino exactly.
    public static readonly Guid ServiceGuid = new("6f2a1b40-9c3e-4d17-8a52-b1c7e0f43d91");
    public static readonly Guid StateCharGuid = new("6f2a1b41-9c3e-4d17-8a52-b1c7e0f43d91");

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(20);
    // Upper bound on one connect attempt (pair + session + GATT resolve). A WinRT GATT call can
    // hang indefinitely in a bad state; without this the maintenance loop wedges until restart.
    private static readonly TimeSpan ConnectPhaseTimeout = TimeSpan.FromSeconds(30);
    // How long to wait for ConnectionStatus to flip to Connected before forcing GATT discovery.
    private static readonly TimeSpan ConnectWaitTimeout = TimeSpan.FromSeconds(10);
    // Cap on each WinRT unpair call, so uninstalling never stalls on an unreachable device.
    private static readonly TimeSpan UnpairTimeout = TimeSpan.FromSeconds(5);
    // Cap on the shutdown teardown, so tray Exit can never leave the process hanging.
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    // After this many consecutive failed cycles, warn the user once that the device is missing.
    private const int MissingDeviceThreshold = 3;
    // After this many failed direct-to-MAC attempts, fall back to advertisement discovery.
    private const int DirectConnectFailureThreshold = 3;
    // Watchdog: this many consecutive write failures means the link is stale, not flaky.
    private const int WriteFailureThreshold = 3;

    private readonly Config _config;
    private readonly BleDevicePresence _presence;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Task? _loopTask;
    private System.Threading.Timer? _heartbeatTimer;

    // Live GATT session objects for the current connection (all null when disconnected).
    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private GattDeviceService? _onAirService;
    private GattDeviceService? _batteryService;
    private GattCharacteristic? _stateChar;
    private GattCharacteristic? _batteryChar;

    // Handlers kept in fields so teardown can detach them — a handler left attached keeps a stale
    // WinRT object alive and still delivering events after the link is gone.
    private TypedEventHandler<BluetoothLEDevice, object>? _connStatusHandler;
    private TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? _batteryValueHandler;

    // Both edges of ConnectionStatusChanged, as awaitables.
    private TaskCompletionSource<bool>? _connectedTcs;
    private TaskCompletionSource<bool>? _disconnectedTcs;

    // Cancels just the current connect/scan cycle (used by "Reconnect now" / "Re-pair" / watchdog).
    private CancellationTokenSource? _cycleCts;

    private readonly object _stateGate = new();
    private byte _desiredState;
    private int _consecutiveFailures;
    private int _consecutiveWriteFailures;
    private bool _missingNotified;
    private bool _bondWarningShown;
    private bool _disposed;

    // Touched only from the single maintenance task.
    private int _directConnectFailures;

    // Events consumed by the app context / tray UI.
    public event Action<BleConnectionState>? ConnectionStateChanged;
    public event Action<int>? BatteryLevelChanged;
    public event Action<string, string>? UserNotification; // (title, message)

    public BleConnectionState State { get; private set; } = BleConnectionState.Disconnected;
    public int? BatteryPercent { get; private set; }

    public BleController(Config config)
    {
        _config = config;
        _presence = new BleDevicePresence(ServiceGuid);
        _presence.DeviceSeen += OnDeviceSeen;
        if (TryParseMac(config.DeviceMac, out ulong saved))
            _presence.SetExpectedAddress(saved);
    }

    /// <summary>Starts the presence watchers, the background maintenance loop and the heartbeat.</summary>
    public void Start()
    {
        _presence.Start();

        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(30, _config.HeartbeatSeconds));
        _heartbeatTimer = new System.Threading.Timer(_ => OnHeartbeat(), null, interval, interval);
        _loopTask = Task.Run(() => MaintainAsync(_cts.Token));
        Logger.Info("BLE controller started.");
    }

    /// <summary>
    /// Sets the desired state byte (bit0 camera, bit1 microphone). If connected, it is written
    /// immediately; otherwise it is written as soon as the next connection is established.
    /// </summary>
    public void SetDesiredState(byte state)
    {
        lock (_stateGate)
            _desiredState = state;

        // Fire-and-forget an immediate write; the loop will re-sync on next connect if we're down.
        _ = WriteStateAsync(state);
    }

    /// <summary>Forces an immediate reconnect attempt instead of waiting out the backoff.</summary>
    public void RequestReconnect()
    {
        Logger.Info("Manual reconnect requested.");
        lock (_stateGate) _consecutiveFailures = 0;
        CancelCurrentCycle();
    }

    /// <summary>Cancels the in-flight connect/scan cycle if one is active (ignores a disposed CTS
    /// during the brief gap between cycles).</summary>
    private void CancelCurrentCycle()
    {
        try { _cycleCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Unpairs the device (clearing the saved MAC) and restarts discovery for a fresh pairing.</summary>
    public async Task RepairAsync()
    {
        Logger.Info("Re-pair requested.");
        string mac = _config.DeviceMac;
        if (!string.IsNullOrEmpty(mac))
        {
            await UnpairByMacAsync(mac);
            _config.DeviceMac = string.Empty;
            _config.Save();
        }

        // Forget the target so we go back to advertisement discovery from scratch.
        _presence.SetExpectedAddress(null);
        _directConnectFailures = DirectConnectFailureThreshold;
        lock (_stateGate) { _consecutiveFailures = 0; _missingNotified = false; }
        CancelCurrentCycle(); // drop the current cycle and rediscover.
    }

    private void OnDeviceSeen(ulong address, DeviceSeenVia via) =>
        Logger.Debug($"Presence: device seen via {Describe(via)}: {FormatMac(address)}.");

    private static string Describe(DeviceSeenVia via) =>
        via == DeviceSeenVia.Advertisement ? "advertisement" : "device-watcher";

    // =====================================================================
    // Maintenance loop
    // =====================================================================

    private async Task MaintainAsync(CancellationToken ct)
    {
        TimeSpan backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
            using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _cycleCts = cycleCts;
            CancellationToken cycle = cycleCts.Token;

            bool connected = false;
            try
            {
                connected = await ConnectAndServeAsync(cycle);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // app shutting down.
            }
            catch (OperationCanceledException)
            {
                // Cycle was cancelled by Reconnect/Re-pair/watchdog — loop again immediately.
                backoff = InitialBackoff;
                continue;
            }
            catch (Exception ex)
            {
                Logger.Error("BLE cycle error: " + ex.Message);
            }
            finally
            {
                await TeardownConnectionAsync();
                SetConnectionState(BleConnectionState.Disconnected);
            }

            if (ct.IsCancellationRequested)
                break;

            if (connected)
            {
                // A good session just ended (device went out of range / powered off). Reset the
                // backoff and try to reconnect promptly.
                backoff = InitialBackoff;
                continue;
            }

            NoteFailure();
            Logger.Info($"Reconnecting in {backoff.TotalSeconds:F0}s (or sooner if the device is seen).");

            // Unlike a plain sleep, this returns the instant the device shows up — which is what
            // keeps a grown backoff from stalling the reconnect for up to a minute.
            bool seen = await _presence.WaitForSignalOrDelayAsync(backoff, ct);
            if (seen)
            {
                Logger.Info("Device seen during backoff; retrying immediately.");
                backoff = InitialBackoff;

                // A confirmed sighting always sends us back to the fast direct-to-MAC path. This
                // matters most when the sighting came from the OS-connected watcher: the device is
                // attached and therefore silent, so falling back to advertisement discovery here
                // would wait for a packet that can never arrive.
                _directConnectFailures = 0;
            }
            else
            {
                backoff = NextBackoff(backoff);
            }
        }
    }

    /// <summary>
    /// One full connect cycle: pick a target → connect → (pair) → open session → resolve GATT →
    /// write state → wait until the link drops. Returns true if a connected session was actually
    /// established.
    /// </summary>
    private async Task<bool> ConnectAndServeAsync(CancellationToken ct)
    {
        // --- 1) Pick a target address ------------------------------------------------------
        // A saved MAC is used directly: the device stops advertising once Windows auto-connects
        // to it, so scanning first would wait forever for a packet that never comes.
        ulong address = 0;
        bool direct = _directConnectFailures < DirectConnectFailureThreshold
                      && TryParseMac(_config.DeviceMac, out address);

        if (direct)
        {
            Logger.Info($"Device target via direct-mac: {FormatMac(address)}.");
        }
        else
        {
            SetConnectionState(BleConnectionState.Scanning);
            ulong? discovered = await _presence.WaitForAddressAsync(ScanTimeout, ct);
            if (discovered is null)
            {
                Logger.Debug("Device not seen within the discovery window.");
                return false;
            }

            address = discovered.Value;
            _directConnectFailures = 0; // discovery worked; trust the saved MAC again next time.
            Logger.Info($"Device found via discovery: {FormatMac(address)}.");
        }

        // --- 2) Connect, bounded by the connect-phase timeout -------------------------------
        SetConnectionState(BleConnectionState.Connecting);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectPhaseTimeout);

        try
        {
            if (!await EstablishAsync(address, connectCts.Token))
            {
                NoteDirectFailure(direct);
                return false;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the connect-phase timeout fired (a cancelled cycle rethrows to the loop).
            Logger.Warn($"Connect phase timed out after {ConnectPhaseTimeout.TotalSeconds:F0}s; " +
                        "tearing down and retrying.");
            NoteDirectFailure(direct);
            return false;
        }

        // --- 3) Connected ------------------------------------------------------------------
        SetConnectionState(BleConnectionState.Connected);
        _presence.SetConnected(true);
        _directConnectFailures = 0;
        lock (_stateGate)
        {
            _consecutiveFailures = 0;
            _consecutiveWriteFailures = 0;
            _missingNotified = false;
            _bondWarningShown = false;
        }
        Logger.Info("Connected and GATT resolved.");

        // Push the current state right away.
        byte current;
        lock (_stateGate) current = _desiredState;
        await WriteStateAsync(current);

        // Block until the device disconnects or the cycle is cancelled.
        await WaitForDisconnectAsync(_device!, ct);
        return true;
    }

    /// <summary>
    /// Brings the link up for a known address: device object → pairing → GATT session →
    /// wait for Connected → resolve GATT. Every step is bounded by <paramref name="ct"/>.
    /// </summary>
    private async Task<bool> EstablishAsync(ulong address, CancellationToken ct)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct);
        if (_device is null)
        {
            Logger.Warn("FromBluetoothAddressAsync returned null.");
            return false;
        }

        // Subscribe before anything else so no transition is missed.
        AttachConnectionStatusHandler(_device);

        string mac = FormatMac(address);
        Logger.Debug($"Device object created: {mac} ({_device.Name}).");

        if (!await EnsurePairedAsync(_device))
            return false;

        // Persist the MAC after a successful pairing so future runs can connect directly.
        if (!string.Equals(_config.DeviceMac, mac, StringComparison.OrdinalIgnoreCase))
        {
            _config.DeviceMac = mac;
            _config.Save();
        }
        _presence.SetExpectedAddress(address);

        // Hold the connection open and let Windows auto-reconnect when in range.
        _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(ct);
        _gattSession.MaintainConnection = true;

        // MaintainConnection usually brings a bonded device up on its own; if it doesn't report
        // Connected in time, GATT discovery below forces the connection anyway.
        if (await WaitForConnectedAsync(_device, ct))
            Logger.Debug("Link reported Connected.");
        else
            Logger.Debug("Link not reported Connected yet; forcing GATT discovery.");

        return await ResolveGattAsync(_device, ct);
    }

    /// <summary>
    /// Subscribes to <see cref="BluetoothLEDevice.ConnectionStatusChanged"/> and exposes both
    /// edges as awaitables. Handling only the disconnect edge is not enough: on a reconnect the
    /// device object is created while the link is still down and we need to know when it comes up.
    /// </summary>
    private void AttachConnectionStatusHandler(BluetoothLEDevice device)
    {
        _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _connStatusHandler = (d, _) =>
        {
            Logger.Info($"Connection status changed: {d.ConnectionStatus}.");
            if (d.ConnectionStatus == BluetoothConnectionStatus.Connected)
                _connectedTcs?.TrySetResult(true);
            else
                _disconnectedTcs?.TrySetResult(true);
        };
        device.ConnectionStatusChanged += _connStatusHandler;

        // Cover a transition that happened before we subscribed (Windows may already be connected).
        if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
            _connectedTcs.TrySetResult(true);
    }

    /// <summary>Waits (briefly) for the link to report Connected. Never throws on timeout.</summary>
    private async Task<bool> WaitForConnectedAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
            return true;

        Task<bool>? signal = _connectedTcs?.Task;
        if (signal is null)
            return false;

        try
        {
            await signal.WaitAsync(ConnectWaitTimeout, ct);
            return device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch (TimeoutException)
        {
            return device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
    }

    /// <summary>Completes when the device transitions to Disconnected or the cycle is cancelled.</summary>
    private async Task WaitForDisconnectAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        if (device.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            return;

        Task<bool>? signal = _disconnectedTcs?.Task;
        if (signal is null)
            return;

        try
        {
            await signal.WaitAsync(ct);
            Logger.Info("Device disconnected.");
        }
        catch (OperationCanceledException) { /* cycle cancelled */ }
    }

    /// <summary>Ensures the device is bonded; performs a JustWorks (ConfirmOnly) pairing if not.</summary>
    private async Task<bool> EnsurePairedAsync(BluetoothLEDevice device)
    {
        DeviceInformationPairing pairing = device.DeviceInformation.Pairing;
        if (pairing.IsPaired)
            return true;

        Logger.Info("Device not paired; starting JustWorks pairing.");
        DeviceInformationCustomPairing custom = pairing.Custom;

        TypedEventHandler<DeviceInformationCustomPairing, DevicePairingRequestedEventArgs> onRequested =
            (_, args) =>
            {
                // JustWorks: the firmware uses NO_INPUT_NO_OUTPUT, so Windows offers ConfirmOnly.
                if (args.PairingKind == DevicePairingKinds.ConfirmOnly)
                    args.Accept();
            };
        custom.PairingRequested += onRequested;

        try
        {
            DevicePairingResult result = await custom.PairAsync(
                DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Encryption);

            if (result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
            {
                Logger.Info("Pairing succeeded.");
                return true;
            }

            Logger.Warn($"Pairing failed: {result.Status}.");
            Notify("On Air pairing failed", $"Could not pair with the device ({result.Status}). Retrying…");
            return false;
        }
        finally
        {
            custom.PairingRequested -= onRequested;
        }
    }

    /// <summary>
    /// Resolves the state characteristic (uncached, so a firmware GATT change can never be served
    /// from a stale table) and, best-effort, the standard battery characteristic. The service
    /// objects are retained so teardown can dispose them — leaking them holds OS-side handles open
    /// and is what makes writes fail silently after the device restarts.
    /// </summary>
    private async Task<bool> ResolveGattAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        GattDeviceServicesResult svc = await device
            .GetGattServicesForUuidAsync(ServiceGuid, BluetoothCacheMode.Uncached).AsTask(ct);
        if (svc.Status != GattCommunicationStatus.Success || svc.Services.Count == 0)
        {
            Logger.Warn($"On Air service not found (status={svc.Status}).");
            return false;
        }

        _onAirService = svc.Services[0];
        for (int i = 1; i < svc.Services.Count; i++)
            TryDispose(svc.Services[i]);

        GattCharacteristicsResult chr = await _onAirService
            .GetCharacteristicsForUuidAsync(StateCharGuid, BluetoothCacheMode.Uncached).AsTask(ct);
        if (chr.Status != GattCommunicationStatus.Success || chr.Characteristics.Count == 0)
        {
            Logger.Error("State characteristic not found — GATT cache is likely stale after a firmware change.");
            Notify("On Air device needs re-pairing",
                   "The device's Bluetooth profile changed. Remove it in Windows Bluetooth settings, then re-pair.");
            return false;
        }

        _stateChar = chr.Characteristics[0];
        Logger.Info("GATT resolved (service + state characteristic).");

        // Battery is optional; failure here must not block the connection.
        try
        {
            GattDeviceServicesResult batSvc = await device
                .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask(ct);
            if (batSvc.Status == GattCommunicationStatus.Success && batSvc.Services.Count > 0)
            {
                _batteryService = batSvc.Services[0];
                for (int i = 1; i < batSvc.Services.Count; i++)
                    TryDispose(batSvc.Services[i]);

                GattCharacteristicsResult batChr = await _batteryService
                    .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                    .AsTask(ct);
                if (batChr.Status == GattCommunicationStatus.Success && batChr.Characteristics.Count > 0)
                {
                    _batteryChar = batChr.Characteristics[0];
                    await SubscribeBatteryAsync(_batteryChar);
                }
            }
        }
        catch (Exception ex)
        {
            // Includes the connect-phase timeout: the state characteristic is already resolved,
            // so the link is usable and we deliberately keep it.
            Logger.Debug("Battery service unavailable: " + ex.Message);
        }

        return true;
    }

    private async Task SubscribeBatteryAsync(GattCharacteristic battery)
    {
        try
        {
            GattReadResult read = await battery.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status == GattCommunicationStatus.Success)
                ReportBattery(read.Value);

            _batteryValueHandler = (_, args) => ReportBattery(args.CharacteristicValue);
            battery.ValueChanged += _batteryValueHandler;
            await battery.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
        }
        catch (Exception ex)
        {
            Logger.Debug("Battery subscribe failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Reads a battery-level notification. The <see cref="DataReader"/> must be disposed: it is a
    /// WinRT IClosable, and this runs on every notification for the life of the connection, so
    /// leaving it to the finalizer accumulates native resources — an app this idle collects rarely,
    /// so they pile up for a long time before the GC gets to them.
    /// </summary>
    private void ReportBattery(IBuffer buffer)
    {
        try
        {
            Diag.CountBatteryNotification();

            if (buffer.Length < 1)
                return;

            using DataReader reader = DataReader.FromBuffer(buffer);
            int percent = reader.ReadByte();
            BatteryPercent = percent;
            Logger.Debug($"Battery level: {percent}%.");
            BatteryLevelChanged?.Invoke(percent);
        }
        catch { /* malformed value; ignore */ }
    }

    // =====================================================================
    // Writing / heartbeat
    // =====================================================================

    private void OnHeartbeat()
    {
        byte state;
        lock (_stateGate) state = _desiredState;
        // Re-write the current value so the firmware's 30-minute failsafe never trips.
        _ = WriteStateAsync(state, heartbeat: true);
    }

    /// <summary>
    /// Writes a 1-byte state value, serialized so writes never overlap. Every attempt is logged
    /// with its outcome — including the "not connected, nothing was sent" case, which is otherwise
    /// indistinguishable in the log from a successful write.
    /// </summary>
    private async Task WriteStateAsync(byte state, bool heartbeat = false)
    {
        string kind = heartbeat ? "Heartbeat" : "State";

        if (_stateChar is null)
        {
            Logger.Info($"{kind} write 0x{state:X2} skipped: not connected (queued for next connect).");
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            GattCharacteristic? ch = _stateChar; // re-read under the lock in case teardown nulled it.
            if (ch is null)
            {
                Logger.Info($"{kind} write 0x{state:X2} skipped: link torn down (queued for next connect).");
                return;
            }

            Diag.CountStateWrite();
            IBuffer buffer = CryptographicBuffer.CreateFromByteArray(new[] { state });
            GattWriteResult result = await ch.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithResponse);

            if (result.Status == GattCommunicationStatus.Success)
            {
                string line = $"{kind} write 0x{state:X2} ok (status=Success).";
                if (heartbeat) Logger.Debug(line); else Logger.Info(line);
                NoteWriteSuccess();
                return;
            }

            if (result.Status == GattCommunicationStatus.ProtocolError)
            {
                // A protocol error on an encrypted characteristic usually means the bond was lost.
                string code = result.ProtocolError is byte pe ? $"0x{pe:X2}" : "n/a";
                Logger.Warn($"{kind} write 0x{state:X2} rejected: status=ProtocolError, protocolError={code}.");
                NotifyBondLostOnce();
            }
            else
            {
                Logger.Warn($"{kind} write 0x{state:X2} failed: status={result.Status}.");
            }

            NoteWriteFailure();
        }
        catch (Exception ex)
        {
            // Link dropped mid-write; the maintenance loop will notice and reconnect.
            Logger.Warn($"{kind} write 0x{state:X2} threw: {ex.Message}");
            NoteWriteFailure();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void NoteWriteSuccess()
    {
        lock (_stateGate) _consecutiveWriteFailures = 0;
    }

    /// <summary>
    /// Watchdog: a run of failed writes while we still believe we are connected means the link is
    /// stale (device restarted behind our back, handles no longer valid). Drop the whole cycle so
    /// everything is disposed and rebuilt from scratch.
    /// </summary>
    private void NoteWriteFailure()
    {
        bool stale;
        lock (_stateGate)
        {
            _consecutiveWriteFailures++;
            stale = State == BleConnectionState.Connected && _consecutiveWriteFailures >= WriteFailureThreshold;
            if (stale)
                _consecutiveWriteFailures = 0;
        }

        if (stale)
        {
            Logger.Warn($"{WriteFailureThreshold} consecutive write failures; " +
                        "treating the link as stale and reconnecting from scratch.");
            CancelCurrentCycle();
        }
    }

    /// <summary>Raises the "bond may be lost" balloon at most once per connection.</summary>
    private void NotifyBondLostOnce()
    {
        lock (_stateGate)
        {
            if (_bondWarningShown)
                return;
            _bondWarningShown = true;
        }

        Notify("On Air write rejected",
               "The device rejected the update (bond may be lost). Try Re-pair device from the tray menu.");
    }

    // =====================================================================
    // Teardown / helpers
    // =====================================================================

    /// <summary>
    /// Releases everything belonging to the current connection, in an order that matters:
    /// event handlers first (so stale objects stop receiving events), then characteristics,
    /// then services, then the session, then the device. Nothing is ever reused on reconnect.
    /// </summary>
    private async Task TeardownConnectionAsync()
    {
        // 1) Detach handlers before anything is disposed.
        if (_device != null && _connStatusHandler != null)
        {
            try { _device.ConnectionStatusChanged -= _connStatusHandler; } catch { }
        }
        _connStatusHandler = null;

        if (_batteryChar != null && _batteryValueHandler != null)
        {
            try { _batteryChar.ValueChanged -= _batteryValueHandler; } catch { }
        }
        _batteryValueHandler = null;

        // Release anyone waiting on a connection edge.
        _connectedTcs?.TrySetResult(false);
        _disconnectedTcs?.TrySetResult(true);
        _connectedTcs = null;
        _disconnectedTcs = null;

        // 2) Drop the characteristics under the write lock so no write can race the teardown.
        //    ConfigureAwait(false): Dispose() may run on the UI thread after the message loop ends,
        //    and posting this continuation back there would deadlock shutdown.
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _stateChar = null;
            _batteryChar = null;
        }
        finally
        {
            _writeLock.Release();
        }

        // 3) Services, then session, then device.
        TryDispose(_onAirService);
        _onAirService = null;
        TryDispose(_batteryService);
        _batteryService = null;

        if (_gattSession != null)
        {
            try { _gattSession.MaintainConnection = false; _gattSession.Dispose(); } catch { }
            _gattSession = null;
        }
        if (_device != null)
        {
            try { _device.Dispose(); } catch { }
            _device = null;
        }

        BatteryPercent = null;
        _presence.SetConnected(false);
    }

    private static void TryDispose(GattDeviceService? service)
    {
        if (service is null)
            return;
        try { service.Dispose(); } catch { }
    }

    private void NoteDirectFailure(bool wasDirect)
    {
        if (!wasDirect)
            return;

        _directConnectFailures++;
        if (_directConnectFailures >= DirectConnectFailureThreshold)
            Logger.Info($"Direct connect failed {_directConnectFailures}x; falling back to discovery.");
    }

    private void NoteFailure()
    {
        bool warn = false;
        lock (_stateGate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= MissingDeviceThreshold && !_missingNotified)
            {
                _missingNotified = true;
                warn = true;
            }
        }
        if (warn)
            Notify("On Air device not found",
                   "The device hasn't been seen for a while. Check that it's powered on and in range.");
    }

    private void SetConnectionState(BleConnectionState state)
    {
        if (State == state)
            return;
        State = state;
        ConnectionStateChanged?.Invoke(state);
    }

    private void Notify(string title, string message)
    {
        Logger.Warn($"{title}: {message}");
        UserNotification?.Invoke(title, message);
    }

    private static TimeSpan NextBackoff(TimeSpan current)
    {
        TimeSpan doubled = current + current;
        return doubled > MaxBackoff ? MaxBackoff : doubled;
    }

    // =====================================================================
    // MAC helpers / static unpair (also used by the uninstaller)
    // =====================================================================

    /// <summary>Formats a WinRT 48-bit Bluetooth address as "AA:BB:CC:DD:EE:FF".</summary>
    public static string FormatMac(ulong address)
    {
        byte[] b = BitConverter.GetBytes(address); // little-endian; top two bytes are 0.
        return $"{b[5]:X2}:{b[4]:X2}:{b[3]:X2}:{b[2]:X2}:{b[1]:X2}:{b[0]:X2}";
    }

    /// <summary>Parses "AA:BB:CC:DD:EE:FF" back into a WinRT 48-bit address; returns false if malformed.</summary>
    public static bool TryParseMac(string mac, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(mac))
            return false;

        string[] parts = mac.Split(':', '-');
        if (parts.Length != 6)
            return false;

        ulong value = 0;
        foreach (string part in parts)
        {
            if (!byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out byte octet))
                return false;
            value = (value << 8) | octet;
        }
        address = value;
        return true;
    }

    /// <summary>
    /// Unpairs the device with the given MAC. Best-effort; safe to call when disconnected.
    /// </summary>
    /// <remarks>
    /// Two things matter for the uninstall path. <c>ConfigureAwait(false)</c> keeps the
    /// continuations off any caller's synchronization context — blocking on this from the setup
    /// window's UI thread used to deadlock the whole app. And each WinRT call is bounded, because a
    /// device that is switched off or out of range must not hold an uninstall hostage.
    /// </remarks>
    public static async Task UnpairByMacAsync(string mac)
    {
        if (!TryParseMac(mac, out ulong address))
            return;

        try
        {
            BluetoothLEDevice? device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                .AsTask().WaitAsync(UnpairTimeout).ConfigureAwait(false);
            if (device is null)
                return;

            try
            {
                if (device.DeviceInformation.Pairing.IsPaired)
                {
                    DeviceUnpairingResult result = await device.DeviceInformation.Pairing.UnpairAsync()
                        .AsTask().WaitAsync(UnpairTimeout).ConfigureAwait(false);
                    Logger.Info($"Unpair {mac}: {result.Status}.");
                }
            }
            finally
            {
                device.Dispose();
            }
        }
        catch (TimeoutException)
        {
            // Windows keeps the bond; the user can still remove it from Bluetooth settings.
            Logger.Warn($"Unpair {mac} timed out after {UnpairTimeout.TotalSeconds:F0}s; continuing.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Unpair {mac} failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        _heartbeatTimer?.Dispose();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }

        // Started on the thread pool so there is no synchronization context to capture, and
        // bounded so an in-flight GATT write holding _writeLock cannot wedge tray Exit. Anything
        // still undisposed here is reclaimed by the OS as the process exits.
        try { Task.Run(TeardownConnectionAsync).Wait(ShutdownTimeout); } catch { }
        _presence.DeviceSeen -= OnDeviceSeen;
        _presence.Dispose();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
