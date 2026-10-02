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
    // TerminalDataRoot is the per-installation GUID folder under
    // %APPDATA%\MetaQuotes\Terminal\. Broker is the server-side folder
    // name under bases\ (e.g. "OANDATMS-MT5"). Both are needed to
    // resolve bases\{broker}\ticks\{symbol}\YYYYMM.tkc.
    public string Mt5TerminalDataRoot { get; set; } = "";
    public string Mt5TerminalBroker   { get; set; } = "";
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
                case "MASSIVE_PROVIDER_API_KEY":
                    MassiveApiKey = value;
                    break;
                case "YAHOO_PROVIDER_API_KEY":
                    YahooApiKey = value;
                    break;
                case "TWELVEDATA_API_KEY":
                    TwelveDataApiKey = value;
                    break;
                case "TIINGO_API_KEY":
                    TiingoApiKey = value;
                    break;
                case "FINNHUB_API_KEY":
                    FinnhubApiKey = value;
                    break;
                case "FCS_PUBLIC_API_KEY":
                    FcsApiKey = value;
                    break;
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