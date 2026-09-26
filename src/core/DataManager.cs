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
    string Source,
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

public readonly record struct DataProgress(
    string Message,
    bool IsDeterminate,
    double Percent);

public class DataManager
{
    private readonly CacheManager _diskCache;
    private readonly IMemoryCache _memoryCache;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConfigManager _config;

    public DataManager(
        CacheManager diskCache,
        IMemoryCache memoryCache,
        IServiceProvider serviceProvider,
        ConfigManager config)
    {
        _diskCache = diskCache;
        _memoryCache = memoryCache;
        _serviceProvider = serviceProvider;
        _config = config;
    }

    public async Task<List<Kline>> GetSymbolDataAsync(string symbol, IProgress<string>? progress = null)
        => await GetSymbolDataAsync(symbol, "1D", progress);

    public async Task<List<Kline>> GetSymbolDataAsync(
        string symbol,
        string timeframe,
        IProgress<string>? progress = null)
    {
        var adapter = progress == null
            ? null
            : new Progress<DataProgress>(p => progress.Report(p.Message));

        return await GetSymbolDataAsync(symbol, timeframe, adapter);
    }

    public async Task<List<Kline>> GetSymbolDataAsync(
        string symbol,
        string timeframe,
        IProgress<DataProgress>? progress)
    {
        var cacheKey = $"{symbol}@{timeframe}";

        if (_memoryCache.TryGetValue(cacheKey, out List<Kline>? memData) && memData != null)
        {
            LogManager.Debug(LogTag.Data, $"RAM hit for {cacheKey} ({memData.Count} bars)");
            progress?.Report(new DataProgress("Loaded from RAM.", false, 100));
            return memData;
        }

        LogManager.Debug(LogTag.Data, $"RAM miss for {cacheKey}");

        var diskData = await _diskCache.LoadCacheAsync(cacheKey);
        if (diskData != null && diskData.Count > 0)
        {
            LogManager.Info(LogTag.Data, $"disk hit for {cacheKey} ({diskData.Count} bars)");
            progress?.Report(new DataProgress("Loaded from Disk.", false, 100));
            _memoryCache.Set(cacheKey, diskData, TimeSpan.FromMinutes(10));
            return diskData;
        }

        var order = _config.Settings.ProviderOrder ?? new List<string>();
        if (order.Count == 0)
        {
            LogManager.Warn(LogTag.Data, "no providers configured in ProviderOrder");
            progress?.Report(new DataProgress("No providers configured.", false, 0));
            return new List<Kline>();
        }

        LogManager.Info(LogTag.Data, $"network fetch for {cacheKey}, {order.Count} providers in chain");

        int total = order.Count;
        int index = 0;

        foreach (var key in order)
        {
            index++;
            double pct = (double)index / total * 100.0;

            IDataProvider provider;
            try
            {
                provider = _serviceProvider.GetRequiredKeyedService<IDataProvider>(key);
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.Data, $"provider '{key}' not registered", ex);
                progress?.Report(new DataProgress($"Provider '{key}' not registered.", true, pct));
                continue;
            }

            LogManager.Debug(LogTag.Data, $"attempt {index}/{total}: {provider.Name} for {cacheKey}");

            progress?.Report(new DataProgress($"Trying {provider.Name}...", true, pct));
            List<Kline> fetched;
            try
            {
                fetched = await provider.FetchAsync(symbol, timeframe);
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.Data, $"{provider.Name} threw for {cacheKey}", ex);
                progress?.Report(new DataProgress($"{provider.Name} failed. Trying next...", true, pct));
                continue;
            }

            if (fetched != null && fetched.Count > 0)
            {
                LogManager.Info(LogTag.Data, $"{provider.Name} returned {fetched.Count} bars for {cacheKey}");
                progress?.Report(new DataProgress($"{provider.Name} returned {fetched.Count} bars. Saving...", true, 100));
                _memoryCache.Set(cacheKey, fetched, TimeSpan.FromMinutes(10));
                await _diskCache.SaveCacheAsync(cacheKey, fetched);
                return fetched;
            }

            LogManager.Warn(LogTag.Data, $"{provider.Name} returned empty for {cacheKey}, falling back");
            progress?.Report(new DataProgress($"{provider.Name} returned no data. Trying next...", true, pct));
        }

        LogManager.Warn(LogTag.Data, $"all providers exhausted for {cacheKey}, no data");
        progress?.Report(new DataProgress("All providers exhausted — no data.", false, 0));
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