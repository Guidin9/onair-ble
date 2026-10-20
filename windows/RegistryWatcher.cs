using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OnAirBle;

/// <summary>
/// Watches a single registry key (and its whole subtree) for changes using the Win32
/// <c>RegNotifyChangeKeyValue</c> API, which is event-driven: the OS signals an event
/// handle when something under the key changes, so we spend zero CPU while idle.
///
/// The API is one-shot — after each notification it must be re-armed — so we register a
/// thread-pool wait that forwards the change to a caller-supplied signal and immediately
/// re-arms. If arming fails, <see cref="TryCreate"/> returns null and the caller falls
/// back to polling.
/// </summary>
internal sealed class RegistryWatcher : IDisposable
{
    // RegNotifyChangeKeyValue filter flags.
    private const int REG_NOTIFY_CHANGE_NAME = 0x00000001;     // subkeys added/removed
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x00000004; // value written
    private const int REG_NOTIFY_FILTER = REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int notifyFilter,
        SafeWaitHandle hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    private readonly RegistryKey _key;
    private readonly AutoResetEvent _regEvent = new(false);
    private readonly Action _onChanged;
    private RegisteredWaitHandle? _registration;
    private volatile bool _disposed;

    private RegistryWatcher(RegistryKey key, Action onChanged)
    {
        _key = key;
        _onChanged = onChanged;
    }

    /// <summary>
    /// Opens the key under the given hive and arms the first notification. Returns null if
    /// the key does not exist or the notification could not be armed.
    /// </summary>
    /// <param name="onChanged">Invoked (on a thread-pool thread) each time the subtree changes.</param>
    public static RegistryWatcher? TryCreate(RegistryHive hive, string path, Action onChanged)
    {
        try
        {
            // The base key is only needed to reach the subkey; the subkey is an independent handle
            // and stays valid after it is released.
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            RegistryKey? key = baseKey.OpenSubKey(path);
            if (key == null)
                return null;

            var watcher = new RegistryWatcher(key, onChanged);

            // When _regEvent is signalled, OnChanged runs on the thread pool (executeOnlyOnce=false
            // keeps the registration alive across re-arms).
            watcher._registration = ThreadPool.RegisterWaitForSingleObject(
                watcher._regEvent, watcher.OnEventSignalled, null, Timeout.Infinite, executeOnlyOnce: false);

            if (!watcher.Arm())
            {
                watcher.Dispose();
                return null;
            }

            return watcher;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>(Re)arms the change notification. Returns true on success (ERROR_SUCCESS).</summary>
    private bool Arm()
    {
        if (_disposed)
            return false;

        int result = RegNotifyChangeKeyValue(
            _key.Handle, watchSubtree: true, REG_NOTIFY_FILTER, _regEvent.SafeWaitHandle, asynchronous: true);
        return result == 0;
    }

    private void OnEventSignalled(object? state, bool timedOut)
    {
        if (_disposed)
            return;

        // Notify the caller, then re-arm for the next change (the API is one-shot).
        try { _onChanged(); } catch { /* the monitor logs its own errors */ }
        Arm();
    }

    public void Dispose()
    {
        _disposed = true;
        _registration?.Unregister(null);
        _regEvent.Dispose();
        _key.Dispose();
    }
}
