using System;
using System.IO;
using System.Windows.Forms;

namespace OnAirBle;

/// <summary>
/// Central definition of the on-disk locations the app uses. Everything lives under
/// %LOCALAPPDATA%\OnAir so install/uninstall can create and remove a single tree
/// without touching anything else, and without needing admin rights.
/// </summary>
internal static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\OnAir — the install root.</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnAir");

    /// <summary>Full path of the installed copy of the executable.</summary>
    public static string InstalledExe => Path.Combine(InstallDir, "OnAirIndicator.exe");

    /// <summary>Configuration file path.</summary>
    public static string ConfigFile => Path.Combine(InstallDir, "config.json");

    /// <summary>Directory that holds the daily rotating log files.</summary>
    public static string LogDir => Path.Combine(InstallDir, "logs");

    /// <summary>Full path of the currently running executable.</summary>
    public static string CurrentExe => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>True when the running process is the copy inside the install directory.</summary>
    public static bool RunningFromInstallDir =>
        string.Equals(
            Path.GetFullPath(CurrentExe).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(InstalledExe).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
