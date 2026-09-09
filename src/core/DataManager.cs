using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using backs2.Core.Providers;

namespace backs2.Core;

public record DataEntry(string Symbol, string Status, string Path);

public class DataManager
{
    private readonly List<IDataProvider> _providers = new();
    
    public DataManager(ConfigManager config) { }

    public List<DataEntry> GetAvailableData()
    {
        if (!Directory.Exists("data")) return new List<DataEntry>();
        return Directory.GetFiles("data/", "*.csv").Select(f => new DataEntry(
            Path.GetFileNameWithoutExtension(f), "Valid", f)).ToList();
    }
}