using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace backs2.Core;

public class AppSettings
{
    public int MaxThreads { get; set; } = 5;

    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;

    public List<string> ProviderOrder { get; set; } = new()
    { "Twelve Data", "Tiingo", "Yahoo", "Massive", "Binance", "Finnhub", "FCS API" };

    public string ActiveAccountFirmFile { get; set; } = "";
    public string ActiveAccountPlan { get; set; } = "";
    public string ActiveAccountSize { get; set; } = "";

    public string LogLevel { get; set; } = "Info";

    public List<string> QueuedDatasets { get; set; } = new();

    // MT5 raw tick bridge
    public string Mt5Host      { get; set; } = "127.0.0.1";
    public int    Mt5Port      { get; set; } = 5555;
    public int    Mt5TimeoutMs { get; set; } = 60000;

    // MT5 .tkc cache extraction (Extract from .tkc subtab).
    public string Mt5TerminalDataRoot { get; set; } = "";
    public string Mt5TerminalBroker   { get; set; } = "";

    // ==================== Theme ====================
    // ARGB hex strings. Parsed by Avalonia.Media.Color.TryParse and
    // written into Application.Current.Resources by ThemeApplier.
    public string ThemeAppBgColor          { get; set; } = "#FF0D1117";
    public string ThemePanelBgColor        { get; set; } = "#FF161B22";
    public string ThemeElevatedColor       { get; set; } = "#FF21262D";
    public string ThemeSunkenColor         { get; set; } = "#FF000000";
    public string ThemeTextPrimaryColor    { get; set; } = "#FFE6EDF3";
    public string ThemeTextSecondaryColor  { get; set; } = "#FF848D97";
    public string ThemeTextDisabledColor   { get; set; } = "#FF484F58";
    public string ThemeBorderColor         { get; set; } = "#FF30363D";
    public string ThemeAccentColor         { get; set; } = "#FF2F81F7";
    public string ThemeAccentHoverColor    { get; set; } = "#FF458EF7";
    public string ThemeAccentPressedColor  { get; set; } = "#FF176BE0";
    public string ThemeSemanticSuccessColor { get; set; } = "#FF238636";
    public string ThemeSemanticWarnColor   { get; set; } = "#FFD29922";
    public string ThemeSemanticDangerColor { get; set; } = "#FFF85149";

    // ==================== Optimizer ====================
    // Bounded Cartesian grid cap. Above this, the optimizer samples the grid
    // instead of running it in full. Default 500 per Phase 26 design.
    public int OptimizerMaxCombinations { get; set; } = 500;

    // Iterations used during the search phase (fast, rank-only).
    public int OptimizerCoarseIterations { get; set; } = 100;

    // Iterations used for the refine pass on the top survivors (full).
    public int OptimizerRefineIterations { get; set; } = 1000;

    // Number of survivors to carry into the refine pass.
    public int OptimizerRefineTopN { get; set; } = 20;

    // CPU=1.0. Future GPU MC path sets this to the expected speedup factor.
    // Only the MC component of the ETA estimate is divided by this value.
    public double OptimizerGpuFactor { get; set; } = 1.0;
}

public class ConfigManager
{
    private static readonly string[] PlaceholderKeys = { "key", "placeholder", "todo", "xxx" };

    private readonly string _configPath = "config/settings.json";
    private readonly string _envPath = ".env";

    public AppSettings Settings { get; private set; } = new();

    public string ConfigDirectory => "config";

    public string MassiveApiKey { get; private set; } = "";
    public string YahooApiKey { get; private set; } = "";
    public string TwelveDataApiKey { get; private set; } = "";
    public string TiingoApiKey { get; private set; } = "";
    public string FinnhubApiKey { get; private set; } = "";
    public string FcsApiKey { get; private set; } = "";

    public ConfigManager()
    {
        LoadEnv();
        LoadConfig();
    }

    private void LoadEnv()
    {
        if (!File.Exists(_envPath)) return;

        foreach (var raw in File.ReadAllLines(_envPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var idx = line.IndexOf('=');
            if (idx <= 0) continue;

            var key = line.Substring(0, idx).Trim();
            var value = NormalizeValue(line.Substring(idx + 1));

            switch (key)
            {
                case "MASSIVE_PROVIDER_API_KEY": MassiveApiKey = value; break;
                case "YAHOO_PROVIDER_API_KEY":   YahooApiKey = value; break;
                case "TWELVEDATA_API_KEY":       TwelveDataApiKey = value; break;
                case "TIINGO_API_KEY":           TiingoApiKey = value; break;
                case "FINNHUB_API_KEY":          FinnhubApiKey = value; break;
                case "FCS_PUBLIC_API_KEY":       FcsApiKey = value; break;
            }
        }
    }

    private static string NormalizeValue(string raw)
    {
        var v = raw.Trim().Trim('"');
        if (v.Length == 0) return "";

        foreach (var placeholder in PlaceholderKeys)
        {
            if (string.Equals(v, placeholder, StringComparison.OrdinalIgnoreCase))
                return "";
        }

        return v;
    }

    public void LoadConfig()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_configPath));
                if (loaded != null) Settings = loaded;
            }
            catch
            {
                Settings = new AppSettings();
            }
        }
        else
        {
            SaveConfig();
        }

        if (Settings.ProviderOrder == null || Settings.ProviderOrder.Count == 0)
            Settings.ProviderOrder = new List<string>
            { "Twelve Data", "Tiingo", "Yahoo", "Massive", "Binance", "Finnhub", "FCS API" };

        Settings.ActiveAccountFirmFile ??= "";
        Settings.ActiveAccountPlan ??= "";
        Settings.ActiveAccountSize ??= "";
        Settings.LogLevel ??= "Info";
        Settings.QueuedDatasets ??= new List<string>();
        Settings.Mt5Host ??= "127.0.0.1";
        Settings.Mt5TerminalDataRoot ??= "";
        Settings.Mt5TerminalBroker ??= "";

        // Theme defaults — populated when an older settings.json
        // (written before the theme system existed) is loaded.
        Settings.ThemeAppBgColor ??= "#FF0D1117";
        Settings.ThemePanelBgColor ??= "#FF161B22";
        Settings.ThemeElevatedColor ??= "#FF21262D";
        Settings.ThemeSunkenColor ??= "#FF000000";
        Settings.ThemeTextPrimaryColor ??= "#FFE6EDF3";
        Settings.ThemeTextSecondaryColor ??= "#FF848D97";
        Settings.ThemeTextDisabledColor ??= "#FF484F58";
        Settings.ThemeBorderColor ??= "#FF30363D";
        Settings.ThemeAccentColor ??= "#FF2F81F7";
        Settings.ThemeAccentHoverColor ??= "#FF458EF7";
        Settings.ThemeAccentPressedColor ??= "#FF176BE0";
        Settings.ThemeSemanticSuccessColor ??= "#FF238636";
        Settings.ThemeSemanticWarnColor ??= "#FFD29922";
        Settings.ThemeSemanticDangerColor ??= "#FFF85149";

        // Optimizer defaults. Value types default to 0 on deserialization of
        // an older settings.json; bump zeros to the shipped defaults. Zero is
        // never a valid value for any of these.
        if (Settings.OptimizerMaxCombinations <= 0)   Settings.OptimizerMaxCombinations = 500;
        if (Settings.OptimizerCoarseIterations <= 0)  Settings.OptimizerCoarseIterations = 100;
        if (Settings.OptimizerRefineIterations <= 0)  Settings.OptimizerRefineIterations = 1000;
        if (Settings.OptimizerRefineTopN <= 0)        Settings.OptimizerRefineTopN = 20;
        if (Settings.OptimizerGpuFactor <= 0)         Settings.OptimizerGpuFactor = 1.0;
    }

    public void SaveConfig()
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(
            _configPath,
            JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}