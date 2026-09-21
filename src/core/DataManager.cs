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

public record DataEntry(
    string Symbol,
    string Source,          // "Cache" or "CSV"
    string Path,
    long SizeBytes,
    DateTime LastModifiedUtc);

public class DataManager
{
    private readonly CacheManager _diskCache;
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConfigManager _config;
    private readonly LogManager _logger;

    public DataManager(
        CacheManager diskCache,
        IMemoryCache memoryCache,
        IServiceProvider serviceProvider,
        ConfigManager config,
        LogManager logger)
    {
        _diskCache = diskCache;
        _memoryCache = memoryCache;
        _serviceProvider = serviceProvider;
        _config = config;
        _logger = logger;
    }

    public async Task<List<Kline>> GetSymbolDataAsync(string symbol, IProgress<string>? progress = null)
    {
        if (_memoryCache.TryGetValue(symbol, out List<Kline>? memData) && memData != null)
        {
            _logger.Info($"DataManager: RAM hit for {symbol} ({memData.Count} bars)");
            progress?.Report("Loaded from RAM.");
            return memData;
        }

        _logger.Info($"DataManager: RAM miss for {symbol}");

        var diskData = await _diskCache.LoadCacheAsync(symbol);
        if (diskData != null && diskData.Count > 0)
        {
            progress?.Report("Loaded from Disk.");
            _memoryCache.Set(symbol, diskData, TimeSpan.FromMinutes(10));
            return diskData;
        }

        var order = _config.Settings.ProviderOrder ?? new List<string>();
        if (order.Count == 0)
        {
            progress?.Report("No providers configured.");
            return new List<Kline>();
        }

        foreach (var key in order)
        {
            IDataProvider provider;
            try
            {
                provider = _serviceProvider.GetRequiredKeyedService<IDataProvider>(key);
            }
            catch (Exception ex)
            {
                _logger.Error($"DataManager: provider '{key}' not registered — {ex.Message}");
                progress?.Report($"Provider '{key}' not registered.");
                continue;
            }

            progress?.Report($"Cache miss. Trying {provider.Name}...");
            List<Kline> fetched;
            try
            {
                fetched = await provider.FetchAsync(symbol);
            }
            catch (Exception ex)
            {
                _logger.Error($"DataManager: {provider.Name} threw — {ex.Message}");
                progress?.Report($"{provider.Name} failed. Trying next...");
                continue;
            }

            if (fetched != null && fetched.Count > 0)
            {
                progress?.Report($"{provider.Name} returned {fetched.Count} bars. Saving...");
                _memoryCache.Set(symbol, fetched, TimeSpan.FromMinutes(10));
                await _diskCache.SaveCacheAsync(symbol, fetched);
                return fetched;
            }

            _logger.Info($"DataManager: {provider.Name} returned no data for {symbol}.");
            progress?.Report($"{provider.Name} returned no data. Trying next...");
        }

        progress?.Report("All providers exhausted — no data.");
        return new List<Kline>();
    }

    // Full catalogue of on-disk datasets: JSON caches + CSV imports.
    public List<DataEntry> GetAvailableData()
    {
        var entries = new List<DataEntry>();

        // 1. JSON caches under data/cache/
        const string cacheDir = "data/cache";
        if (Directory.Exists(cacheDir))
        {
            foreach (var file in Directory.GetFiles(cacheDir, "*.json"))
            {
                var info = new FileInfo(file);
                entries.Add(new DataEntry(
                    Symbol: Path.GetFileNameWithoutExtension(info.Name),
                    Source: "Cache",
                    Path: info.FullName,
                    SizeBytes: info.Length,
                    LastModifiedUtc: info.LastWriteTimeUtc));
            }
        }

        // 2. CSV imports directly under data/
        if (Directory.Exists("data"))
        {
            foreach (var file in Directory.GetFiles("data", "*.csv"))
            {
                var info = new FileInfo(file);
                entries.Add(new DataEntry(
                    Symbol: Path.GetFileNameWithoutExtension(info.Name),
                    Source: "CSV",
                    Path: info.FullName,
                    SizeBytes: info.Length,
                    LastModifiedUtc: info.LastWriteTimeUtc));
            }
        }

        return entries
            .OrderBy(e => e.Source)
            .ThenBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Just the cached symbol names — used by the Strategies tab picker.
    public List<string> GetCachedSymbols()
    {
        const string cacheDir = "data/cache";
        if (!Directory.Exists(cacheDir)) return new List<string>();

        return Directory.GetFiles(cacheDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }
}