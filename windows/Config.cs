using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnAirBle;

/// <summary>Log verbosity, ordered so that a level includes everything above it.</summary>
internal enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
}

/// <summary>
/// User-editable configuration, persisted as %LOCALAPPDATA%\OnAir\config.json.
/// All properties have sensible defaults so a missing or partial file still yields a
/// working configuration (forward/backward compatible).
/// </summary>
internal sealed class Config
{
    /// <summary>
    /// Bluetooth MAC of the paired device, "AA:BB:CC:DD:EE:FF". Empty until the first
    /// successful pairing; once known, the controller connects to it directly instead of
    /// waiting to rediscover it by advertisement.
    /// </summary>
    [JsonPropertyName("deviceMac")]
    public string DeviceMac { get; set; } = string.Empty;

    /// <summary>
    /// How often (seconds) to re-write the current state byte even when nothing changed.
    /// Must stay well below the firmware's 30-minute failsafe. Default 300 (5 min).
    /// </summary>
    [JsonPropertyName("heartbeatSeconds")]
    public int HeartbeatSeconds { get; set; } = 300;

    /// <summary>Whether the tray icon is shown. When false, a hidden message pump keeps the app alive.</summary>
    [JsonPropertyName("showTrayIcon")]
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Log verbosity: Error, Warn, Info, or Debug.</summary>
    [JsonPropertyName("logLevel")]
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Loads config.json, falling back to defaults if the file is missing or corrupt.
    /// Never throws — a bad config must not stop the app from starting.
    /// </summary>
    public static Config Load()
    {
        try
        {
            string path = AppPaths.ConfigFile;
            if (!File.Exists(path))
                return new Config();

            string json = File.ReadAllText(path);
            Config? cfg = JsonSerializer.Deserialize<Config>(json, JsonOptions);
            return cfg ?? new Config();
        }
        catch
        {
            // Corrupt/unreadable config -> defaults. (Logger may not be up yet.)
            return new Config();
        }
    }

    /// <summary>Writes the current values to config.json, creating the install directory if needed.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.InstallDir);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(AppPaths.ConfigFile, json);
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to save config.json: " + ex.Message);
        }
    }
}
