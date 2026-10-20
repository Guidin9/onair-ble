using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OnAirBle;

/// <summary>
/// The tiny window shown when the EXE is launched with no arguments: it lets the user choose
/// Install, Uninstall, or Cancel.
///
/// The work is awaited, never blocked on. Blocking the UI thread here froze the whole window —
/// the installer awaits BLE calls whose continuations are posted back to this thread's message
/// queue, so a blocked pump could never deliver them.
/// </summary>
internal sealed class SetupWindow : Form
{
    private readonly Button _installButton;
    private readonly Button _uninstallButton;
    private readonly Button _cancelButton;
    private readonly Label _status;

    private bool _busy;

    public int ExitCode { get; private set; }

    public SetupWindow()
    {
        Text = "On Air — Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(360, 178);

        var title = new Label
        {
            Text = "On Air indicator",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 14),
        };

        var subtitle = new Label
        {
            Text = "Mirrors your camera/microphone status to the\nBluetooth \"On Air\" light.",
            AutoSize = true,
            Location = new Point(16, 44),
        };

        _status = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Size = new Size(328, 18),
            Location = new Point(16, 90),
            ForeColor = SystemColors.GrayText,
        };

        _installButton = new Button
        {
            Text = "Install",
            Size = new Size(100, 32),
            Location = new Point(16, 118),
        };
        _installButton.Click += async (_, _) => await RunAsync("Installing…", Installer.InstallAsync);

        _uninstallButton = new Button
        {
            Text = "Uninstall",
            Size = new Size(100, 32),
            Location = new Point(128, 118),
        };
        _uninstallButton.Click += async (_, _) => await RunAsync("Uninstalling…", Installer.UninstallAsync);

        _cancelButton = new Button
        {
            Text = "Cancel",
            Size = new Size(100, 32),
            Location = new Point(240, 118),
            DialogResult = DialogResult.Cancel,
        };
        _cancelButton.Click += (_, _) => Close();

        Controls.Add(title);
        Controls.Add(subtitle);
        Controls.Add(_status);
        Controls.Add(_installButton);
        Controls.Add(_uninstallButton);
        Controls.Add(_cancelButton);

        AcceptButton = _installButton;
        CancelButton = _cancelButton;
    }

    /// <summary>
    /// Runs an installer action while keeping the window alive and responsive, then reports the
    /// result and closes.
    /// </summary>
    private async Task RunAsync(string busyText, Func<Task<InstallerResult>> action)
    {
        if (_busy)
            return;

        SetBusy(true, busyText);

        InstallerResult result;
        try
        {
            result = await action();
        }
        catch (Exception ex)
        {
            // These run from an async void event handler, so an escaping exception would take the
            // process down. Everything is turned into a result the user can read.
            Logger.Error("Setup action failed: " + ex);
            result = new InstallerResult(false, "The operation failed:\n" + ex.Message);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }

        ExitCode = result.Success ? 0 : 1;
        MessageBox.Show(this, result.Message, "On Air", MessageBoxButtons.OK,
                        result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        Close();
    }

    private void SetBusy(bool busy, string statusText)
    {
        _busy = busy;
        _installButton.Enabled = !busy;
        _uninstallButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
        _status.Text = statusText;
        UseWaitCursor = busy;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Interrupting a half-finished install/uninstall leaves a worse mess than waiting it out.
        if (_busy && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }
}
