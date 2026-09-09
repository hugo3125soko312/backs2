using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using System.Text.Json; namespace backs2.Core;  public class ConfigManager {     private string _configPath = "config/settings.json";     public AppSettings Settings { get; set; } = new() { MaxThreads = 5 };      public void LoadConfig() {         if(File.Exists(_configPath)) Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_configPath))!;     }     public void SaveConfig() => File.WriteAllText(_configPath, JsonSerializer.Serialize(Settings)); }  public class AppSettings {     public int MaxThreads { get; set; }     public string YahooApiKey { get; set; } = ""; }
