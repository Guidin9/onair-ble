using System;
using System.Threading;
using System.Windows.Forms;

namespace OnAirBle;

/// <summary>
/// Entry point and command-line dispatch for the On Air BLE companion app.
///
/// A single self-contained EXE plays four roles depending on its argument:
///   --install    install into %LOCALAPPDATA%\OnAir and register autostart
///   --uninstall  reverse the install (and unpair the BLE device)
///   --run        run the windowless background app (this is what autostart calls)
///   (no args)    show a tiny Install / Uninstall / Cancel window
///
/// The background app deliberately runs in the interactive user session (NOT a
/// Windows Service): WinRT Bluetooth is unreliable in Session 0, and the
/// CapabilityAccessManager consent keys we read live under HKCU for the logged-in
/// user. Autostart is achieved via the HKCU ...\Run key, so no admin rights are
/// ever required.
/// </summary>
internal static class Program
{
    /// <summary>Value name used for the HKCU ...\Run autostart entry and the mutex.</summary>
    internal const string AppId = "OnAirIndicator";

    /// <summary>Global single-instance mutex name (per interactive session is fine here).</summary>
    private const string MutexName = @"Global\OnAirIndicator_SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;

        try
        {
            switch (mode)
            {
                case "--install":
                    return Installer.Install();

                case "--uninstall":
                    return Installer.Uninstall();

                case "--run":
                    return RunBackground();

                default:
                    // No recognised argument -> interactive setup chooser.
                    return RunSetupWindow();
            }
        }
        catch (Exception ex)
        {
            // Last-resort guard so a startup failure is at least recorded.
            try { Logger.Error("Fatal error in Main: " + ex); } catch { /* ignore */ }
            return 1;
        }
    }

    /// <summary>
    /// Runs the windowless background application. Guards against a second instance,
    /// installs global exception handlers, and hands control to the WinForms message
    /// loop via <see cref="OnAirAppContext"/> (which owns the tray icon, the camera/mic
    /// monitor, the BLE controller, and the system-event hooks).
    /// </summary>
    private static int RunBackground()
    {
        // Single-instance guard: autostart plus a manual double-click must not run twice.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isNew);
        if (!isNew)
            return 0; // another instance already owns the app; exit quietly.

        Config config = Config.Load();
        Logger.Initialize(config.LogLevel);
        Logger.Info("=== On Air BLE indicator starting (--run) ===");

        InstallGlobalExceptionHandlers();

        try
        {
            using var appContext = new OnAirAppContext(config);
            Application.Run(appContext);
        }
        finally
        {
            Logger.Info("=== On Air BLE indicator stopped ===");
        }

        return 0;
    }

    /// <summary>Shows the no-argument Install / Uninstall / Cancel window.</summary>
    private static int RunSetupWindow()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var window = new SetupWindow();
        Application.Run(window);
        return window.ExitCode;
    }

    /// <summary>
    /// Wires process-wide exception handlers so an unexpected error is logged and, where
    /// possible, does not tear the whole app down. The camera/mic and BLE layers already
    /// swallow their own I/O errors; these are the final safety net.
    /// </summary>
    private static void InstallGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Error("Unhandled AppDomain exception: " + (e.ExceptionObject as Exception)?.ToString());
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Unobserved task exception: " + e.Exception);
            e.SetObserved(); // prevent the process from being torn down.
        };

        Application.ThreadException += (_, e) =>
        {
            Logger.Error("WinForms thread exception: " + e.Exception);
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    }
}
