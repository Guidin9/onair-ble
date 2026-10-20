using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace OnAirBle;

/// <summary>How the device was observed to be present.</summary>
internal enum DeviceSeenVia
{
    /// <summary>A BLE advertisement carrying our service UUID was received.</summary>
    Advertisement,

    /// <summary>Windows reported the paired device as connected at the OS level.</summary>
    DeviceWatcher,
}

/// <summary>
/// Long-lived "the device is here" signal source, deliberately kept *outside* the BLE
/// maintenance loop so it is never torn down between connect attempts.
///
/// Two independent sources feed it, because neither alone is sufficient:
///   * <see cref="BluetoothLEAdvertisementWatcher"/> — the only way to find a device that has
///     never been paired. Useless once the device is connected, because the single-host
///     firmware stops advertising while a host is attached.
///   * <see cref="DeviceWatcher"/> over paired BLE endpoints, watching
///     System.Devices.Aep.IsConnected — this is what catches the case where *Windows itself*
///     auto-connected to the bonded device, so no advertisement will ever arrive.
///
/// The advertisement watcher runs whenever the link is down and is paused while connected
/// (the device does not advertise then, so scanning would be pure radio/battery waste).
/// The paired-device watcher is a cheap PnP enumeration and runs continuously.
///
/// Callers either wait for a concrete address (<see cref="WaitForAddressAsync"/>, used for
/// first-time discovery) or use <see cref="WaitForSignalOrDelayAsync"/> in place of a plain
/// backoff sleep, so a reconnect delay is cut short the instant the device reappears.
/// </summary>
internal sealed class BleDevicePresence : IDisposable
{
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";

    private readonly Guid _serviceGuid;
    private readonly object _gate = new();

    // Association-endpoint id -> Bluetooth address. Resolving an id costs a WinRT round trip and
    // Updated fires often for every paired peripheral, so each id is resolved at most once.
    // A cached 0 means "could not be resolved" and suppresses retry storms.
    private readonly Dictionary<string, ulong> _addressByDeviceId = new(StringComparer.OrdinalIgnoreCase);

    private BluetoothLEAdvertisementWatcher? _adWatcher;
    private DeviceWatcher? _deviceWatcher;

    /// <summary>Completed when the device is seen; replaced once a waiter has consumed it.</summary>
    private TaskCompletionSource<ulong> _seen = NewSignal();

    /// <summary>Address of the device we are looking for, or 0 when nothing is paired yet.</summary>
    private ulong _expectedAddress;

    private bool _connected;
    private bool _started;
    private bool _disposed;

    /// <summary>Raised once per consumed sighting (not once per advertisement packet).</summary>
    public event Action<ulong, DeviceSeenVia>? DeviceSeen;

    public BleDevicePresence(Guid serviceGuid) => _serviceGuid = serviceGuid;

    /// <summary>
    /// Sets the address the paired-device watcher should match against. Without it we cannot tell
    /// our device from any other paired BLE peripheral, so that source stays silent. Clearing it
    /// (on re-pair) also drops the id cache so a re-bonded device is resolved afresh.
    /// </summary>
    public void SetExpectedAddress(ulong? address)
    {
        lock (_gate)
        {
            _expectedAddress = address ?? 0;
            _addressByDeviceId.Clear();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
                return;
            _started = true;
        }

        StartAdvertisementWatcher();
        StartDeviceWatcher();
    }

    /// <summary>Pauses/resumes advertisement scanning as the link comes up and goes down.</summary>
    public void SetConnected(bool connected)
    {
        lock (_gate)
        {
            if (_connected == connected)
                return;
            _connected = connected;
        }

        if (connected)
            PauseAdvertisementWatcher();
        else
            ResumeAdvertisementWatcher();
    }

    // =====================================================================
    // Waiting
    // =====================================================================

    /// <summary>
    /// Waits for the device to be seen and returns its address, or null if it was not seen within
    /// <paramref name="timeout"/>. Used for first-time discovery and as the fallback path when
    /// connecting straight to the saved MAC keeps failing.
    /// </summary>
    /// <remarks>
    /// Cancellation propagates deliberately: a cancelled cycle ("Reconnect now" / "Re-pair") must
    /// unwind to the maintenance loop and retry at once, not be mistaken for "device not seen" and
    /// pushed into a backoff sleep.
    /// </remarks>
    public async Task<ulong?> WaitForAddressAsync(TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource<ulong> signal;
        lock (_gate) signal = _seen;

        try
        {
            return await signal.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            ConsumeSignal(signal);
        }
    }

    /// <summary>
    /// Sleeps for <paramref name="delay"/>, but returns as soon as the device is seen. Returns true
    /// if it was seen (the caller should retry immediately and reset its backoff), false if the
    /// full delay elapsed. This is what stops a grown backoff from stalling a reconnect for up to a
    /// minute after the device comes back.
    /// </summary>
    public async Task<bool> WaitForSignalOrDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        TaskCompletionSource<ulong> signal;
        lock (_gate) signal = _seen;

        try
        {
            await signal.Task.WaitAsync(delay, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false; // shutting down; the caller re-checks its own token.
        }
        finally
        {
            ConsumeSignal(signal);
        }
    }

    private void Signal(ulong address, DeviceSeenVia via)
    {
        TaskCompletionSource<ulong> pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            pending = _seen;
        }

        // TrySetResult fails when the current signal is already raised and not yet consumed —
        // that also keeps a stream of advertisements from spamming the event and the log.
        if (!pending.TrySetResult(address))
            return;

        Diag.CountPresenceSignal();
        try { DeviceSeen?.Invoke(address, via); } catch { /* subscriber logs its own errors */ }
    }

    /// <summary>Arms a fresh signal once a waiter has taken delivery of the current one.</summary>
    private void ConsumeSignal(TaskCompletionSource<ulong> consumed)
    {
        lock (_gate)
        {
            if (!_disposed && consumed.Task.IsCompleted && ReferenceEquals(_seen, consumed))
                _seen = NewSignal();
        }
    }

    private static TaskCompletionSource<ulong> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // =====================================================================
    // Advertisement watcher
    // =====================================================================

    private void StartAdvertisementWatcher()
    {
        try
        {
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(_serviceGuid);
            watcher.Received += (_, args) => Signal(args.BluetoothAddress, DeviceSeenVia.Advertisement);
            watcher.Stopped += (_, args) =>
            {
                // Windows stops the watcher on radio off/aborted scans; bring it back if we still
                // want to be scanning.
                Logger.Debug($"Advertisement watcher stopped ({args.Error}).");
                ResumeAdvertisementWatcher();
            };

            lock (_gate) _adWatcher = watcher;
            ResumeAdvertisementWatcher();
        }
        catch (Exception ex)
        {
            Logger.Warn("Advertisement watcher could not be created: " + ex.Message);
        }
    }

    private void ResumeAdvertisementWatcher()
    {
        BluetoothLEAdvertisementWatcher? watcher;
        lock (_gate)
        {
            if (_disposed || _connected)
                return;
            watcher = _adWatcher;
        }
        if (watcher is null)
            return;

        try
        {
            if (watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
            {
                watcher.Start();
                Logger.Debug("Advertisement watcher started.");
            }
        }
        catch (Exception ex)
        {
            // Start() throws while the watcher is still Stopping; the Stopped handler retries.
            Logger.Debug("Advertisement watcher start failed: " + ex.Message);
        }
    }

    private void PauseAdvertisementWatcher()
    {
        BluetoothLEAdvertisementWatcher? watcher;
        lock (_gate) watcher = _adWatcher;
        if (watcher is null)
            return;

        try
        {
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                watcher.Stop();
                Logger.Debug("Advertisement watcher paused (link is up).");
            }
        }
        catch { /* already stopping */ }
    }

    // =====================================================================
    // Paired-device watcher (OS-level connection detection)
    // =====================================================================

    private void StartDeviceWatcher()
    {
        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            DeviceWatcher watcher = DeviceInformation.CreateWatcher(
                selector,
                new[] { IsConnectedProperty },
                DeviceInformationKind.AssociationEndpoint);

            watcher.Added += (_, info) => OnDeviceReport(info.Id, info.Properties);
            watcher.Updated += (_, update) => OnDeviceReport(update.Id, update.Properties);

            lock (_gate) _deviceWatcher = watcher;
            watcher.Start();
            Logger.Debug("Paired-device watcher started.");
        }
        catch (Exception ex)
        {
            Logger.Warn("Paired-device watcher could not be started: " + ex.Message);
        }
    }

    private void OnDeviceReport(string id, IReadOnlyDictionary<string, object> properties)
    {
        if (!properties.TryGetValue(IsConnectedProperty, out object? value) ||
            value is not bool isConnected || !isConnected)
        {
            return;
        }

        ulong expected;
        bool haveCached;
        ulong cached;
        lock (_gate)
        {
            if (_disposed)
                return;
            expected = _expectedAddress;
            haveCached = _addressByDeviceId.TryGetValue(id, out cached);
        }

        if (expected == 0)
            return; // nothing paired yet; this source cannot identify our device.

        if (!haveCached)
        {
            _ = ResolveAddressAsync(id, expected);
            return;
        }

        if (cached == expected)
            Signal(expected, DeviceSeenVia.DeviceWatcher);
    }

    private async Task ResolveAddressAsync(string id, ulong expected)
    {
        ulong address = 0;
        try
        {
            BluetoothLEDevice? device = await BluetoothLEDevice.FromIdAsync(id);
            if (device != null)
            {
                address = device.BluetoothAddress;
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not resolve device id to an address: {ex.Message}");
        }

        lock (_gate)
        {
            if (_disposed)
                return;
            _addressByDeviceId[id] = address;
        }

        if (address != 0 && address == expected)
            Signal(address, DeviceSeenVia.DeviceWatcher);
    }

    public void Dispose()
    {
        BluetoothLEAdvertisementWatcher? adWatcher;
        DeviceWatcher? deviceWatcher;
        TaskCompletionSource<ulong> signal;

        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            adWatcher = _adWatcher;
            deviceWatcher = _deviceWatcher;
            signal = _seen;
            _adWatcher = null;
            _deviceWatcher = null;
        }

        try { adWatcher?.Stop(); } catch { }
        try { deviceWatcher?.Stop(); } catch { }
        signal.TrySetCanceled();
    }
}
