using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OnAirBle;

/// <summary>
/// The system-tray presence: a status icon (colour reflects connection / camera state), a
/// context menu for field troubleshooting, and balloon notifications for critical errors.
/// All members must be touched on the UI thread; the app context marshals callers accordingly.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly Func<string> _statusTextProvider;
    private readonly List<IntPtr> _iconHandles = new();

    private readonly Icon _iconDisconnected;
    private readonly Icon _iconConnecting;
    private readonly Icon _iconConnectedIdle;
    private readonly Icon _iconActive;

    private int? _battery;

    public TrayIcon(Func<string> statusTextProvider, Action onReconnect, Action onRepair, Action onExit)
    {
        _statusTextProvider = statusTextProvider;

        // Pre-build the four status icons (grey/amber/green/red dots).
        _iconDisconnected = MakeDotIcon(Color.Gray);
        _iconConnecting = MakeDotIcon(Color.Goldenrod);
        _iconConnectedIdle = MakeDotIcon(Color.SeaGreen);
        _iconActive = MakeDotIcon(Color.Firebrick);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Status", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Re-pair device", null, (_, _) => onRepair());
        menu.Items.Add("Reconnect now", null, (_, _) => onReconnect());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open logs", null, (_, _) => OpenPath(AppPaths.LogDir));
        menu.Items.Add("Open config.json", null, (_, _) => OpenPath(AppPaths.ConfigFile));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => onExit());

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconDisconnected,
            Text = "On Air — starting…",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatus();
    }

    /// <summary>Updates the icon and tooltip for the current connection / hardware state.</summary>
    public void UpdateState(BleConnectionState state, bool hardwareActive)
    {
        Icon icon = state switch
        {
            BleConnectionState.Connected => hardwareActive ? _iconActive : _iconConnectedIdle,
            BleConnectionState.Connecting => _iconConnecting,
            BleConnectionState.Scanning => _iconConnecting,
            _ => _iconDisconnected,
        };
        _notifyIcon.Icon = icon;

        string bat = _battery is int p ? $" · {p}%" : string.Empty;
        string hw = state == BleConnectionState.Connected && hardwareActive ? " · ON AIR" : string.Empty;
        // NotifyIcon.Text is limited to 63 characters.
        _notifyIcon.Text = Truncate($"On Air — {state}{hw}{bat}", 63);
    }

    public void SetBattery(int percent)
    {
        _battery = percent;
        // Refresh the tooltip suffix without changing the icon.
        _notifyIcon.Text = Truncate(_notifyIcon.Text.Split(" · ")[0] + $" · {percent}%", 63);
    }

    /// <summary>Shows a balloon/toast for a critical condition.</summary>
    public void ShowBalloon(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(8000);
    }

    private void ShowStatus() =>
        MessageBox.Show(_statusTextProvider(), "On Air — Status", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static void OpenPath(string path)
    {
        try
        {
            // For a file, ensure it exists; for the log dir, create it if missing.
            if (Directory.Exists(path) || File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                Logger.Warn($"Cannot open '{path}' — it does not exist yet.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to open '{path}': {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Renders a small filled-circle icon of the given colour.</summary>
    private Icon MakeDotIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 12, 12);
            using var pen = new Pen(Color.FromArgb(80, 0, 0, 0));
            g.DrawEllipse(pen, 2, 2, 12, 12);
        }

        IntPtr hIcon = bmp.GetHicon();
        _iconHandles.Add(hIcon); // tracked so we can DestroyIcon on dispose.

        // FromHandle does not own hIcon, so the wrapper is disposed here and the clone — which owns
        // its own copy — is what we hand out and dispose later.
        using Icon wrapper = Icon.FromHandle(hIcon);
        return (Icon)wrapper.Clone();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _iconDisconnected.Dispose();
        _iconConnecting.Dispose();
        _iconConnectedIdle.Dispose();
        _iconActive.Dispose();
        foreach (IntPtr h in _iconHandles)
        {
            try { DestroyIcon(h); } catch { }
        }
        _iconHandles.Clear();
    }
}
