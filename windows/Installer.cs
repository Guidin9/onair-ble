using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OnAirBle;

/// <summary>Outcome of an install/uninstall run, with the message to present to the user.</summary>
internal readonly record struct InstallerResult(bool Success, string Message);

/// <summary>
/// Implements the self-install / self-uninstall behaviour. The same EXE is both the app
/// and its installer; nothing here needs administrator rights because everything lives
/// under %LOCALAPPDATA% and autostart uses the HKCU ...\Run key.
///
/// The async methods are the real implementation and show no UI of their own — presenting the
/// result is the caller's job. Two rules keep the setup window from freezing:
///   * every blocking step (killing processes, copying a ~135 MB EXE, deleting the tree) runs
///     inside <see cref="Task.Run"/>, so the message pump keeps running;
///   * nothing ever blocks on a task from the UI thread. Blocking on <c>UnpairByMacAsync</c>
///     from a button handler used to deadlock outright: its continuation was posted to the UI
///     queue that the blocked thread was supposed to be pumping.
/// </summary>
internal static class Installer
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    // =====================================================================
    // Command-line entry points
    // =====================================================================

    public static int Install() => RunFromCommandLine(InstallAsync);

    public static int Uninstall() => RunFromCommandLine(UninstallAsync);

    /// <summary>
    /// Runs an installer action for the <c>--install</c> / <c>--uninstall</c> command line and
    /// reports the result. Starting the work with <see cref="Task.Run"/> means it begins on a
    /// thread-pool thread with no synchronization context to capture, so blocking here is safe.
    /// </summary>
    private static int RunFromCommandLine(Func<Task<InstallerResult>> action)
    {
        InstallerResult result = Task.Run(action).GetAwaiter().GetResult();

        if (result.Success)
            ShowInfo(result.Message);
        else
            ShowError(result.Message);

        return result.Success ? 0 : 1;
    }

    // =====================================================================
    // Install
    // =====================================================================

    public static async Task<InstallerResult> InstallAsync()
    {
        try
        {
            Logger.Initialize(LogLevel.Info);
            Logger.Info("=== Install starting ===");

            await Task.Run(() =>
            {
                // 1) Ensure the install directory exists.
                Directory.CreateDirectory(AppPaths.InstallDir);

                // Stop any previously installed copy so we can overwrite its EXE.
                StopRunningInstances();

                // 2) Copy this running EXE into the install directory (unless we already are it).
                if (!AppPaths.RunningFromInstallDir)
                {
                    File.Copy(AppPaths.CurrentExe, AppPaths.InstalledExe, overwrite: true);
                    Logger.Info($"Copied EXE to {AppPaths.InstalledExe}.");
                }

                // 3) Register autostart: HKCU ...\Run -> "<installed exe>" --run
                SetRunKey();

                // 4) Write a default config only if one doesn't already exist (preserve on reinstall).
                if (!File.Exists(AppPaths.ConfigFile))
                {
                    new Config().Save();
                    Logger.Info("Default config.json written.");
                }

                // 5) Launch the installed copy immediately so the user needn't reboot.
                Process.Start(new ProcessStartInfo(AppPaths.InstalledExe, "--run") { UseShellExecute = false });
                Logger.Info("Installed app launched.");
            }).ConfigureAwait(false);

            return new InstallerResult(true,
                "On Air was installed and is now running in the background.\n\n" +
                "It will start automatically each time you sign in.");
        }
        catch (Exception ex)
        {
            Logger.Error("Install failed: " + ex);
            return new InstallerResult(false, "Install failed:\n" + ex.Message);
        }
    }

    // =====================================================================
    // Uninstall
    // =====================================================================

    public static async Task<InstallerResult> UninstallAsync()
    {
        try
        {
            Logger.Initialize(LogLevel.Info);
            Logger.Info("=== Uninstall starting ===");

            // 1) Stop the running background instance(s) so the folder/EXE can be removed and the
            //    device can be unpaired from here, and drop the autostart entry.
            string mac = await Task.Run(() =>
            {
                StopRunningInstances();
                RemoveRunKey();
                return Config.Load().DeviceMac;
            }).ConfigureAwait(false);

            // 2) Unpair the BLE device (leave it clean) using the saved MAC. Bounded internally, so
            //    a device that is switched off cannot hold the uninstall hostage.
            if (!string.IsNullOrEmpty(mac))
            {
                try { await BleController.UnpairByMacAsync(mac).ConfigureAwait(false); }
                catch (Exception ex) { Logger.Warn("Unpair during uninstall failed: " + ex.Message); }
            }

            // 3) Delete the install directory. A running EXE cannot delete itself, so if we ARE
            //    the installed copy we schedule the removal to happen after we exit.
            await Task.Run(() =>
            {
                if (AppPaths.RunningFromInstallDir)
                {
                    Logger.Info("Scheduling deferred self-cleanup.");
                    ScheduleDeferredCleanup();
                }
                else
                {
                    // Logged first: the log file lives inside the directory being removed.
                    Logger.Info("Removing install directory.");
                    DeleteInstallDir();
                }
            }).ConfigureAwait(false);

            // 4) NOTE: the user's original EXE (the one they double-clicked) is intentionally
            //    left in place so they can run --install again later.
            return new InstallerResult(true,
                "On Air was uninstalled.\n\n" +
                "The program you launched from is still here if you want to reinstall later.");
        }
        catch (Exception ex)
        {
            Logger.Error("Uninstall failed: " + ex);
            return new InstallerResult(false, "Uninstall failed:\n" + ex.Message);
        }
    }

    // =====================================================================
    // Run key
    // =====================================================================

    private static void SetRunKey()
    {
        string command = $"\"{AppPaths.InstalledExe}\" --run";
        using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        run.SetValue(Program.AppId, command, RegistryValueKind.String);
        Logger.Info($"Autostart set: {command}");
    }

    private static void RemoveRunKey()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run?.GetValue(Program.AppId) != null)
            {
                run.DeleteValue(Program.AppId, throwOnMissingValue: false);
                Logger.Info("Autostart entry removed.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to remove autostart entry: " + ex.Message);
        }
    }

    // =====================================================================
    // Process / filesystem helpers
    // =====================================================================

    /// <summary>Terminates any running OnAirIndicator processes except this one.</summary>
    private static void StopRunningInstances()
    {
        int self = Environment.ProcessId;
        foreach (Process p in Process.GetProcessesByName("OnAirIndicator"))
        {
            try
            {
                if (p.Id == self)
                    continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                Logger.Info($"Stopped running instance (pid {p.Id}).");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not stop instance (pid {p.Id}): {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static void DeleteInstallDir()
    {
        if (Directory.Exists(AppPaths.InstallDir))
            Directory.Delete(AppPaths.InstallDir, recursive: true);
    }

    /// <summary>
    /// When the uninstaller is the installed copy, it can't delete its own EXE while running.
    /// We (a) schedule the EXE for deletion on next reboot as a fallback, and (b) spawn a
    /// detached cmd that waits a couple of seconds for us to exit, then removes the whole tree.
    /// The normal case is that the detached cmd succeeds and no reboot is needed.
    /// </summary>
    private static void ScheduleDeferredCleanup()
    {
        // Best-effort reboot fallback for the locked EXE.
        try { MoveFileEx(AppPaths.InstalledExe, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }

        // Detached cleanup: wait for our process to exit, then delete the directory.
        string dir = AppPaths.InstallDir;
        string args = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\"";
        var psi = new ProcessStartInfo("cmd.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);
    }

    // =====================================================================
    // Message boxes (command-line paths only; the setup window shows its own)
    // =====================================================================

    private static void ShowInfo(string message) =>
        MessageBox.Show(message, "On Air", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static void ShowError(string message) =>
        MessageBox.Show(message, "On Air", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
