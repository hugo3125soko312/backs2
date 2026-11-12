using System;
using System.IO;
using System.Reflection;

namespace backs2.Core;

/// <summary>
/// Centralized path management. All file system paths in the application
/// should go through here. Prevents relative path bugs and makes path
/// changes easy.
/// </summary>
public static class AppPaths
{
    static AppPaths()
    {
        // Base directory is next to the executable
        BaseDirectory = AppDomain.CurrentDomain.BaseDirectory ??
                       Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
                       Directory.GetCurrentDirectory();

        // Standard subdirectories
        DataDirectory = Path.Combine(BaseDirectory, "data");
        CacheDirectory = Path.Combine(BaseDirectory, "cache");
        ConfigDirectory = Path.Combine(BaseDirectory, "config");
        OutputDirectory = Path.Combine(BaseDirectory, "output");
        SessionsDirectory = Path.Combine(BaseDirectory, "sessions");
        PresetsDirectory = Path.Combine(BaseDirectory, "presets");
        LogsDirectory = Path.Combine(BaseDirectory, "logs");

        // Walk-forward specific
        WalkForwardDirectory = Path.Combine(DataDirectory, "walkforward");
    }

    public static string BaseDirectory { get; }
    public static string DataDirectory { get; }
    public static string CacheDirectory { get; }
    public static string ConfigDirectory { get; }
    public static string OutputDirectory { get; }
    public static string SessionsDirectory { get; }
    public static string PresetsDirectory { get; }
    public static string LogsDirectory { get; }
    public static string WalkForwardDirectory { get; }

    /// <summary>
    /// Ensures all required directories exist.
    /// </summary>
    public static void Initialize()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(PresetsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(WalkForwardDirectory);
    }

    /// <summary>
    /// Gets the path to a walk-forward batch file.
    /// </summary>
    public static string GetWalkForwardBatchPath(string batchId) =>
        Path.Combine(WalkForwardDirectory, $"{batchId}.json");

    /// <summary>
    /// Gets the path to a specific preset file.
    /// </summary>
    public static string GetPresetPath(string presetName) =>
        Path.Combine(PresetsDirectory, $"{presetName}.json");

    /// <summary>
    /// Gets the path to the main configuration file.
    /// </summary>
    public static string ConfigFilePath => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>
    /// Gets the path to the log file.
    /// </summary>
    public static string LogFilePath => Path.Combine(LogsDirectory, "app.log");
}
