using System;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OnAirBle;

/// <summary>
/// The WinForms <see cref="ApplicationContext"/> that owns the running background app. It
/// hosts the message pump (required for the tray icon and for SystemEvents notifications),
/// wires the camera/mic monitor to the BLE controller, and reacts to power/session events.
///
/// Even when the tray icon is hidden (config.showTrayIcon = false) this context still runs,
/// keeping a hidden synchronizing control alive so SystemEvents keep firing.
/// </summary>
internal sealed class OnAirAppContext : ApplicationContext
{
    /// <summary>How often the debug-only resource health line is emitted.</summary>
    private static readonly TimeSpan HealthInterval = TimeSpan.FromMinutes(5);

    private readonly Config _config;
    private readonly CameraMicMonitor _monitor;
    private readonly BleController _ble;
    private readonly TrayIcon? _tray;

    // Hidden control used purely to marshal callbacks onto the UI thread.
    private readonly Control _sync = new();

    // Debug-only; Diag.LogHealth() samples nothing unless debug logging is on.
    private readonly System.Threading.Timer _healthTimer;

    private bool _disposed;

    public OnAirAppContext(Config config)
    {
        _config = config;

        // Force the marshalling handle to exist on this (UI) thread.
        _sync.CreateControl();

        _ble = new BleController(config);
        _monitor = new CameraMicMonitor(OnCameraMicStateChanged);

        if (config.ShowTrayIcon)
        {
            _tray = new TrayIcon(
                statusTextProvider: BuildStatusText,
                onReconnect: () => _ble.RequestReconnect(),
                onRepair: () => _ = _ble.RepairAsync(),
                onExit: RequestExit);
        }

        // Route BLE events onto the UI thread before touching the tray.
        _ble.ConnectionStateChanged += s => Post(() => _tray?.UpdateState(s, IsHardwareActive()));
        _ble.BatteryLevelChanged += p => Post(() => _tray?.SetBattery(p));
        _ble.UserNotification += (title, msg) => Post(() => _tray?.ShowBalloon(title, msg));

        // React to sleep/wake and lock/unlock.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // Start the workers.
        _ble.Start();
        _monitor.Start();

        _healthTimer = new System.Threading.Timer(_ => Diag.LogHealth(), null, HealthInterval, HealthInterval);

        Logger.Info("Application context initialized.");
    }

    /// <summary>Camera/mic monitor pushed a new state byte — forward it to the device.</summary>
    private void OnCameraMicStateChanged(byte state)
    {
        _ble.SetDesiredState(state);
        Post(() => _tray?.UpdateState(_ble.State, state != 0));
    }

    private bool IsHardwareActive() => _monitor.CurrentState != 0;

    // =====================================================================
    // Power / session events
    // =====================================================================

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            // Coming back from sleep: proactively kick the BLE link instead of waiting out backoff.
            Logger.Info("System resumed from sleep; refreshing BLE connection.");
            _ble.RequestReconnect();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                // Locked: the camera is necessarily off, so force the light off immediately.
                Logger.Info("Session locked; forcing state off.");
                _ble.SetDesiredState(0);
                break;

            case SessionSwitchReason.SessionUnlock:
                // Unlocked: restore whatever the monitor currently reports.
                Logger.Info("Session unlocked; restoring current state.");
                _ble.SetDesiredState(_monitor.CurrentState);
                break;
        }
    }

    // =====================================================================
    // Status / helpers
    // =====================================================================

    private string BuildStatusText()
    {
        byte s = _monitor.CurrentState;
        var sb = new StringBuilder();
        sb.AppendLine($"Connection: {_ble.State}");
        if (!string.IsNullOrEmpty(_config.DeviceMac))
            sb.AppendLine($"Device: {_config.DeviceMac}");
        sb.AppendLine($"Battery: {(_ble.BatteryPercent is int p ? p + "%" : "unknown")}");
        sb.AppendLine($"Camera: {((s & CameraMicMonitor.StateBitCamera) != 0 ? "in use" : "idle")}");
        sb.AppendLine($"Microphone: {((s & CameraMicMonitor.StateBitMicrophone) != 0 ? "in use" : "idle")}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Marshals an action onto the UI thread (no-op safe after disposal).</summary>
    private void Post(Action action)
    {
        try
        {
            if (_sync.IsDisposed)
                return;
            if (_sync.InvokeRequired)
                _sync.BeginInvoke(action);
            else
                action();
        }
        catch { /* shutting down */ }
    }

    /// <summary>Ends the message loop, which unwinds to Dispose and stops the app.</summary>
    private void RequestExit()
    {
        Logger.Info("Exit requested from tray.");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;

            _healthTimer.Dispose();
            _monitor.Dispose();
            _ble.Dispose();
            _tray?.Dispose();
            _sync.Dispose();
        }
        base.Dispose(disposing);
    }
}
