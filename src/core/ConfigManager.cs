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

    public List<string> ProviderOrder { get; set; } = new() { "Twelve Data", "Tiingo", "Yahoo", "Massive" };

    public string ActiveAccountFirmFile { get; set; } = "";
    public string ActiveAccountPlan { get; set; } = "";
    public string ActiveAccountSize { get; set; } = "";

    public string LogLevel { get; set; } = "Info";

    public List<string> QueuedDatasets { get; set; } = new();
}

public class ConfigManager
{
    private readonly string _configPath = "config/settings.json";
    private readonly string _envPath = ".env";

    public AppSettings Settings { get; private set; } = new();

    public string ConfigDirectory => "config";

    public string MassiveApiKey { get; private set; } = "";
    public string YahooApiKey { get; private set; } = "";
    public string TwelveDataApiKey { get; private set; } = "";
    public string TiingoApiKey { get; private set; } = "";

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
            var value = line.Substring(idx + 1).Trim().Trim('"');

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
            }
        }
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
            Settings.ProviderOrder = new List<string> { "Twelve Data", "Tiingo", "Yahoo", "Massive" };

        Settings.ActiveAccountFirmFile ??= "";
        Settings.ActiveAccountPlan ??= "";
        Settings.ActiveAccountSize ??= "";
        Settings.LogLevel ??= "Info";
        Settings.QueuedDatasets ??= new List<string>();
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