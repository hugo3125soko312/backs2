using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace backs2.Core;

public class AppSettings
{
    public int MaxThreads { get; set; } = 5;

    // Resilience Settings
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;

    // Provider Routing (ordered — first success wins)
    public List<string> ProviderOrder { get; set; } = new() { "Yahoo", "Massive" };
}

public class ConfigManager
{
    private readonly string _configPath = "config/settings.json";
    private readonly string _envPath = ".env";

    public AppSettings Settings { get; private set; } = new();

    // Secrets loaded from .env — never persisted to settings.json
    public string MassiveApiKey { get; private set; } = "";
    public string YahooApiKey { get; private set; } = "";

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
            Settings.ProviderOrder = new List<string> { "Yahoo", "Massive" };
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