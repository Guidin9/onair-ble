using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace OnAirBle;

/// <summary>
/// Detects whether the camera and/or microphone are in use, by reading the Windows
/// CapabilityAccessManager consent store. Under each of the "webcam" and "microphone"
/// roots, every app (packaged, or under the "NonPackaged" subkey for classic desktop
/// apps) has a subkey with <c>LastUsedTimeStart</c> / <c>LastUsedTimeStop</c> FILETIME
/// values. A <c>LastUsedTimeStop == 0</c> anywhere in the subtree means that device is in
/// use right now.
///
/// Monitoring is event-driven via <see cref="RegistryWatcher"/> (near-zero idle CPU) with
/// a 2-second polling fallback if the notifications can't be armed. Rapid open/close
/// flapping is smoothed with a short debounce so the far-end LED doesn't flicker.
///
/// The result is packed into the 1-byte state bitfield the firmware expects:
/// bit0 = camera active, bit1 = microphone active.
/// </summary>
internal sealed class CameraMicMonitor : IDisposable
{
    // State bits — must match the firmware (OnAir_BLE_C3.ino: STATE_BIT_CAMERA / STATE_BIT_MICROPHONE).
    public const byte StateBitCamera = 0x01;
    public const byte StateBitMicrophone = 0x02;

    private const string ConsentStoreBase =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string WebcamPath = ConsentStoreBase + @"\webcam";
    private const string MicrophonePath = ConsentStoreBase + @"\microphone";

    // Debounce window: coalesce bursts of registry changes before re-evaluating.
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    // Safety re-read, in case a change notification is ever missed.
    private static readonly TimeSpan SafetyInterval = TimeSpan.FromSeconds(30);
    // Polling cadence used only when event-driven watching couldn't be set up.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Action<byte> _onStateChanged;
    private readonly List<RegistryWatcher> _watchers = new();
    private readonly object _gate = new();

    private System.Threading.Timer? _debounceTimer;   // fires once, Debounce after the latest change.
    private System.Threading.Timer? _safetyTimer;     // periodic re-read regardless of notifications.
    private System.Threading.Timer? _pollTimer;       // fallback only.

    private byte _lastState;
    private bool _hasState;
    private bool _disposed;

    public CameraMicMonitor(Action<byte> onStateChanged)
    {
        _onStateChanged = onStateChanged ?? throw new ArgumentNullException(nameof(onStateChanged));
    }

    /// <summary>The most recently computed state byte (bit0 camera, bit1 microphone).</summary>
    public byte CurrentState
    {
        get { lock (_gate) return _lastState; }
    }

    /// <summary>
    /// Reads the initial state, then arms event-driven watchers on both consent roots
    /// (falling back to polling if none could be armed).
    /// </summary>
    public void Start()
    {
        // Emit the initial state immediately so the device is in sync from the first moment.
        Evaluate(force: true);

        var targets = new (RegistryHive Hive, string Path)[]
        {
            (RegistryHive.CurrentUser, WebcamPath),
            (RegistryHive.CurrentUser, MicrophonePath),
            // HKLM roots also carry some (mostly packaged) entries; harmless if absent.
            (RegistryHive.LocalMachine, WebcamPath),
            (RegistryHive.LocalMachine, MicrophonePath),
        };

        foreach (var t in targets)
        {
            RegistryWatcher? w = RegistryWatcher.TryCreate(t.Hive, t.Path, OnRegistryChanged);
            if (w != null)
                _watchers.Add(w);
        }

        _debounceTimer = new System.Threading.Timer(_ => Evaluate(force: false), null, Timeout.Infinite, Timeout.Infinite);
        _safetyTimer = new System.Threading.Timer(_ => Evaluate(force: false), null, SafetyInterval, SafetyInterval);

        if (_watchers.Count == 0)
        {
            Logger.Warn("RegNotify could not be armed on any consent root; falling back to 2s polling.");
            _pollTimer = new System.Threading.Timer(_ => Evaluate(force: false), null, PollInterval, PollInterval);
        }
        else
        {
            Logger.Info($"Camera/mic monitor active (event-driven, {_watchers.Count} root(s) watched).");
        }
    }

    /// <summary>A watched subtree changed — (re)start the debounce timer.</summary>
    private void OnRegistryChanged()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads current state and, if it changed (or <paramref name="force"/>), notifies the caller.</summary>
    private void Evaluate(bool force)
    {
        Diag.CountEvaluation();

        byte state;
        try
        {
            bool cameraActive =
                IsRootActive(RegistryHive.CurrentUser, WebcamPath) ||
                IsRootActive(RegistryHive.LocalMachine, WebcamPath);
            bool micActive =
                IsRootActive(RegistryHive.CurrentUser, MicrophonePath) ||
                IsRootActive(RegistryHive.LocalMachine, MicrophonePath);

            state = 0;
            if (cameraActive) state |= StateBitCamera;
            if (micActive) state |= StateBitMicrophone;
        }
        catch (Exception ex)
        {
            Logger.Warn("Consent store evaluation failed: " + ex.Message);
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = force || !_hasState || state != _lastState;
            _lastState = state;
            _hasState = true;
        }

        if (changed)
        {
            Logger.Info($"Camera/mic state = 0x{state:X2} " +
                        $"(cam={(state & StateBitCamera) != 0}, mic={(state & StateBitMicrophone) != 0})");
            try { _onStateChanged(state); } catch (Exception ex) { Logger.Warn("State callback failed: " + ex.Message); }
        }
    }

    /// <summary>True if any subkey under the given consent root has LastUsedTimeStop == 0.</summary>
    private static bool IsRootActive(RegistryHive hive, string path)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? root = baseKey.OpenSubKey(path);
            return root != null && ScanForActiveUsage(root);
        }
        catch
        {
            return false; // missing/inaccessible root -> treat as inactive.
        }
    }

    /// <summary>
    /// Recursively walks a key looking for LastUsedTimeStop == 0. Descends into per-app
    /// subkeys and the "NonPackaged" subkey (whose children encode the exe path with '#').
    /// </summary>
    private static bool ScanForActiveUsage(RegistryKey key)
    {
        object? stop = key.GetValue("LastUsedTimeStop");
        if (stop != null && Convert.ToInt64(stop) == 0L)
            return true; // in use right now.

        foreach (string subName in key.GetSubKeyNames())
        {
            try
            {
                using RegistryKey? sub = key.OpenSubKey(subName);
                if (sub != null && ScanForActiveUsage(sub))
                    return true;
            }
            catch
            {
                // Skip an inaccessible subkey and keep scanning the rest.
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _debounceTimer?.Dispose();
        _safetyTimer?.Dispose();
        _pollTimer?.Dispose();
        foreach (RegistryWatcher w in _watchers)
            w.Dispose();
        _watchers.Clear();
    }
}
