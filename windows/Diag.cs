using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace OnAirBle;

/// <summary>
/// Lightweight self-instrumentation for a windowless app, where the log file is the only
/// diagnostic channel available in the field.
///
/// Counters are bumped on the hot paths that repeat forever (consent-store evaluations, battery
/// notifications, GATT writes, presence sightings). <see cref="LogHealth"/> then emits process
/// resource usage next to the <b>deltas</b> of those counters since the previous line. Dividing the
/// handle delta by a counter delta attributes a resource leak to the path causing it — which is
/// exactly how the "handles climb while memory stays flat" case is diagnosed without attaching
/// external tooling to a background process.
///
/// Everything here is skipped entirely unless debug logging is on, so normal runs pay nothing.
/// </summary>
internal static class Diag
{
    public static long Evaluations;          // CameraMicMonitor.Evaluate
    public static long BatteryNotifications; // BleController.ReportBattery
    public static long StateWrites;          // BleController.WriteStateAsync (attempts reaching the characteristic)
    public static long PresenceSignals;      // BleDevicePresence.Signal

    public static void CountEvaluation() => Interlocked.Increment(ref Evaluations);
    public static void CountBatteryNotification() => Interlocked.Increment(ref BatteryNotifications);
    public static void CountStateWrite() => Interlocked.Increment(ref StateWrites);
    public static void CountPresenceSignal() => Interlocked.Increment(ref PresenceSignals);

    // Previous sample, for delta reporting.
    private static long _prevHandles;
    private static long _prevEvaluations;
    private static long _prevBattery;
    private static long _prevWrites;
    private static long _prevPresence;
    private static bool _hasPrevious;

    /// <summary>
    /// Writes one health line at debug level. No-op when debug logging is off — the process is not
    /// even sampled in that case.
    /// </summary>
    public static void LogHealth()
    {
        if (!Logger.IsDebugEnabled)
            return;

        try
        {
            long handles;
            long privateBytes;

            // Process holds a handle of its own; leaving it open would make the diagnostic leak.
            using (Process self = Process.GetCurrentProcess())
            {
                handles = self.HandleCount;
                privateBytes = self.PrivateMemorySize64;
            }

            long evaluations = Interlocked.Read(ref Evaluations);
            long battery = Interlocked.Read(ref BatteryNotifications);
            long writes = Interlocked.Read(ref StateWrites);
            long presence = Interlocked.Read(ref PresenceSignals);

            string line = string.Format(
                CultureInfo.InvariantCulture,
                "Health: handles={0}{1}, priv={2:F1}MB, gcHeap={3:F1}MB, gen0/1/2={4}/{5}/{6}, " +
                "evals={7}{8}, batt={9}{10}, writes={11}{12}, presence={13}{14}",
                handles, Delta(handles, _prevHandles),
                privateBytes / 1024.0 / 1024.0,
                GC.GetTotalMemory(forceFullCollection: false) / 1024.0 / 1024.0,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                evaluations, Delta(evaluations, _prevEvaluations),
                battery, Delta(battery, _prevBattery),
                writes, Delta(writes, _prevWrites),
                presence, Delta(presence, _prevPresence));

            Logger.Debug(line);

            _prevHandles = handles;
            _prevEvaluations = evaluations;
            _prevBattery = battery;
            _prevWrites = writes;
            _prevPresence = presence;
            _hasPrevious = true;
        }
        catch (Exception ex)
        {
            // Diagnostics must never disturb the app.
            Logger.Debug("Health sampling failed: " + ex.Message);
        }
    }

    /// <summary>Renders " (+N)" against the previous sample; empty on the very first line.</summary>
    private static string Delta(long current, long previous) =>
        _hasPrevious ? string.Format(CultureInfo.InvariantCulture, " ({0:+#;-#;+0})", current - previous) : string.Empty;
}
