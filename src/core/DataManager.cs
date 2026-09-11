using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using backs2.Core.Models;
using backs2.Core.Providers;

namespace backs2.Core;

public record DataEntry(string Symbol, string Status, string Path);

public class DataManager
{
    private readonly CacheManager _diskCache;
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConfigManager _config;

    public DataManager(CacheManager diskCache, IMemoryCache memoryCache, IServiceProvider serviceProvider, ConfigManager config)
    {
        _diskCache = diskCache;
        _memoryCache = memoryCache;
        _serviceProvider = serviceProvider;
        _config = config;
    }

    private IDataProvider GetActiveProvider()
    {
        // Resolve the key from settings (defaulting to "Yahoo")
        var providerKey = _config.Settings?.ActiveProviderKey ?? "Yahoo"; 
        return _serviceProvider.GetRequiredKeyedService<IDataProvider>(providerKey);
    }

    public async Task<List<Kline>> GetSymbolDataAsync(string symbol, IProgress<string>? progress = null)
    {
        // 1. Memory Tier (RAM)
        if (_memoryCache.TryGetValue(symbol, out List<Kline>? memData))
        {
            progress?.Report("Loaded from RAM.");
            return memData!;
        }

        // 2. Disk Tier (Persistent JSON/Parquet)
        var diskData = await _diskCache.LoadCacheAsync<List<Kline>>(symbol);
        if (diskData != null)
        {
            progress?.Report("Loaded from Disk.");
            _memoryCache.Set(symbol, diskData, TimeSpan.FromMinutes(10));
            return diskData;
        }

        // 3. Network Tier (Dynamic Provider)
        var provider = GetActiveProvider();
        progress?.Report($"Cache miss. Fetching from {provider.Name}...");
        
        var newData = await provider.FetchAsync(symbol);

        if (newData != null && newData.Count > 0)
        {
            progress?.Report("Saving data...");
            _memoryCache.Set(symbol, newData, TimeSpan.FromMinutes(10));
            await _diskCache.SaveCacheAsync(symbol, newData);
        }

        return newData ?? new List<Kline>();
    }

    public List<DataEntry> GetAvailableData()
    {
        if (!Directory.Exists("data")) return new List<DataEntry>();
        return Directory.GetFiles("data/", "*.csv")
            .Select(f => new DataEntry(Path.GetFileNameWithoutExtension(f), "Valid", f))
            .ToList();
    }
}