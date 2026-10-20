using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace OnAirBle;

/// <summary>
/// Minimal thread-safe file logger. Writes one file per day under
/// %LOCALAPPDATA%\OnAir\logs\onair-YYYY-MM-DD.log, keeps the last 7 days, and prunes
/// older files at startup and whenever the day rolls over. All logging is best-effort:
/// a failure to write must never disturb the app.
/// </summary>
internal static class Logger
{
    private const int RetentionDays = 7;

    private static readonly object Gate = new();
    private static LogLevel _level = LogLevel.Info;
    private static DateTime _currentDay = DateTime.MinValue;

    /// <summary>Sets the active verbosity and performs the first log-directory prune.</summary>
    public static void Initialize(LogLevel level)
    {
        _level = level;
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            PruneOldLogs();
        }
        catch
        {
            // If the log directory can't be created we simply log nothing to disk.
        }
    }

    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);

    /// <summary>
    /// Whether debug output would be written. Lets callers skip building a message — or, for
    /// <see cref="Diag"/>, skip sampling the process entirely — when it would be discarded.
    /// </summary>
    public static bool IsDebugEnabled => _level >= LogLevel.Debug;

    /// <summary>Returns today's log file path (used by the tray "Open logs" action).</summary>
    public static string CurrentLogFile => LogPathFor(DateTime.Now);

    private static string LogPathFor(DateTime when) =>
        Path.Combine(AppPaths.LogDir, $"onair-{when:yyyy-MM-dd}.log");

    private static void Write(LogLevel level, string message)
    {
        if (level > _level)
            return; // below the configured verbosity threshold.

        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] {2}",
            DateTime.Now, level.ToString().ToUpperInvariant(), message);

        lock (Gate)
        {
            try
            {
                DateTime today = DateTime.Now.Date;
                if (today != _currentDay)
                {
                    _currentDay = today;
                    Directory.CreateDirectory(AppPaths.LogDir);
                    PruneOldLogs(); // day rolled over -> drop anything past retention.
                }

                File.AppendAllText(LogPathFor(DateTime.Now), line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Disk full / permission / racing delete: ignore, logging is non-critical.
            }
        }

        // Also echo to the console when one is attached (running a console build interactively).
        try { Console.WriteLine(line); } catch { /* no console */ }
    }

    /// <summary>Deletes log files older than the retention window.</summary>
    private static void PruneOldLogs()
    {
        try
        {
            DateTime cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
            foreach (string file in Directory.EnumerateFiles(AppPaths.LogDir, "onair-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    try { File.Delete(file); } catch { /* keep pruning others */ }
                }
            }
        }
        catch
        {
            // Directory may not exist yet; nothing to prune.
        }
    }
}
