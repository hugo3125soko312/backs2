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
    DateTime LastModifiedUtc)
{
    public string SizeDisplay => FormatSize(SizeBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

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
        => await GetSymbolDataAsync(symbol, "1D", progress);

    public async Task<List<Kline>> GetSymbolDataAsync(
        string symbol,
        string timeframe,
        IProgress<string>? progress = null)
    {
        var cacheKey = $"{symbol}@{timeframe}";

        if (_memoryCache.TryGetValue(cacheKey, out List<Kline>? memData) && memData != null)
        {
            _logger.Info($"DataManager: RAM hit for {cacheKey} ({memData.Count} bars)");
            progress?.Report("Loaded from RAM.");
            return memData;
        }

        _logger.Info($"DataManager: RAM miss for {cacheKey}");

        var diskData = await _diskCache.LoadCacheAsync(cacheKey);
        if (diskData != null && diskData.Count > 0)
        {
            progress?.Report("Loaded from Disk.");
            _memoryCache.Set(cacheKey, diskData, TimeSpan.FromMinutes(10));
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

            progress?.Report($"Trying {provider.Name}...");
            List<Kline> fetched;
            try
            {
                fetched = await provider.FetchAsync(symbol, timeframe);
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
                _memoryCache.Set(cacheKey, fetched, TimeSpan.FromMinutes(10));
                await _diskCache.SaveCacheAsync(cacheKey, fetched);
                return fetched;
            }

            _logger.Info($"DataManager: {provider.Name} returned no data for {cacheKey}.");
            progress?.Report($"{provider.Name} returned no data. Trying next...");
        }

        progress?.Report("All providers exhausted — no data.");
        return new List<Kline>();
    }

    public List<DataEntry> GetAvailableData()
    {
        var entries = new List<DataEntry>();

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

    public List<string> GetCachedSymbols()
    {
        const string cacheDir = "data/cache";
        if (!Directory.Exists(cacheDir)) return new List<string>();

        return Directory.GetFiles(cacheDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Split('@')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}