using System;
using System.IO;
using System.Text.Json;

namespace backs2.Core;

public class AppSettings 
{
    public int MaxThreads { get; set; } = 5;
    public string YahooApiKey { get; set; } = "";
    
    // Resilience Settings
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;

    // Provider Routing
    public string ActiveProviderKey { get; set; } = "Yahoo";
}

public class ConfigManager 
{
    private readonly string _configPath = "config/settings.json";
    public AppSettings Settings { get; private set; } = new();

    public ConfigManager() => LoadConfig();

    public void LoadConfig() 
    {
        if (File.Exists(_configPath)) 
        {
            Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_configPath)) ?? new();
        }
        else
        {
            // Auto-generate default config if missing
            SaveConfig();
        }
    }

    public void SaveConfig() 
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
            
        File.WriteAllText(_configPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}