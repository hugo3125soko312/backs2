using System;
using System.IO;
using System.Text.Json;
using backs2.Core;

namespace backs2.Core;

public class AppSettings 
{
    public int MaxThreads { get; set; } = 5;
    public string YahooApiKey { get; set; } = "";
    
    // Added Resilience Settings
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;
}

public class ConfigManager 
{
    private readonly string _configPath = "config/settings.json";
    public AppSettings Settings { get; private set; } = new();

    public ConfigManager() => LoadConfig();

    public void LoadConfig() 
    {
        if (File.Exists(_configPath)) 
            Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_configPath)) ?? new();
    }

    public void SaveConfig() 
    {
        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}