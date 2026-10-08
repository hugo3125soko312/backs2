using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using backs2.Core.Models;
using backs2.Core.Providers;

namespace backs2.Core;

public record DataEntry(
    string Symbol,
    string Timeframe,
    string Tier,
    string Provider,
    string Path,
    long SizeBytes,
    DateTime LastModifiedLocal,
    bool HasVolume)
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
    private sealed record MemoryEntry(List<Kline> Bars, string Source);

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

    public IDataProvider? GetProvider(string key)
    {
        try
        {
            return _serviceProvider.GetRequiredKeyedService<IDataProvider>(key);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<Kline>> GetSymbolDataAsync(string symbol, IProgress<string>? progress = null)
        => await GetSymbolDataAsync(symbol, "1D", progress, null);

    public async Task<List<Kline>> GetSymbolDataAsync(
        string symbol,
        string timeframe,
        IProgress<string>? progress = null,
        string? preferredProvider = null)
    {
        var adapter = progress == null
            ? null
            : new Progress<DataProgress>(p => progress.Report(p.Message));

        return await GetSymbolDataAsync(symbol, timeframe, adapter, preferredProvider);
    }

    public async Task<List<Kline>> GetSymbolDataAsync(
        string symbol,
        string timeframe,
        IProgress<DataProgress>? progress,
        string? preferredProvider = null)
    {
        var cacheKey = $"{symbol}@{timeframe}";
        bool hasPreference = !string.IsNullOrWhiteSpace(preferredProvider);

        if (_memoryCache.TryGetValue(cacheKey, out MemoryEntry? mem) && mem != null)
        {
            bool match = !hasPreference
                || string.Equals(mem.Source, preferredProvider, StringComparison.OrdinalIgnoreCase);

            if (match)
            {
                LogManager.Debug(LogTag.Data, $"RAM hit for {cacheKey} ({mem.Bars.Count} bars, source {mem.Source})");
                progress?.Report(new DataProgress("Loaded from RAM.", false, 100));
                return mem.Bars;
            }

            LogManager.Debug(LogTag.Data,
                $"RAM hit for {cacheKey} from {mem.Source} but preferred is {preferredProvider}, bypassing");
        }
        else
        {
            LogManager.Debug(LogTag.Data, $"RAM miss for {cacheKey}");
        }

        var disk = await _diskCache.LoadCacheAsync(cacheKey);
        if (disk != null && disk.Bars.Count > 0)
        {
            bool match = !hasPreference
                || string.Equals(disk.Source, preferredProvider, StringComparison.OrdinalIgnoreCase);

            if (match)
            {
                LogManager.Info(LogTag.Data, $"disk hit for {cacheKey} from {disk.Source} ({disk.Bars.Count} bars)");
                progress?.Report(new DataProgress($"Loaded from Disk ({disk.Source}).", false, 100));
                _memoryCache.Set(cacheKey, new MemoryEntry(disk.Bars, disk.Source), TimeSpan.FromMinutes(10));
                return disk.Bars;
            }

            LogManager.Info(LogTag.Data,
                $"disk hit for {cacheKey} from {disk.Source} but preferred is {preferredProvider}, refetching");
        }

        var order = _config.Settings.ProviderOrder ?? new List<string>();
        List<string> walk;

        if (hasPreference)
        {
            walk = new List<string> { preferredProvider! };
            foreach (var k in order)
            {
                if (!string.Equals(k, preferredProvider, StringComparison.OrdinalIgnoreCase))
                    walk.Add(k);
            }
        }
        else
        {
            walk = new List<string>(order);
        }

        if (walk.Count == 0)
        {
            LogManager.Warn(LogTag.Data, "no providers configured in ProviderOrder");
            progress?.Report(new DataProgress("No providers configured.", false, 0));
            return new List<Kline>();
        }

        LogManager.Info(LogTag.Data,
            $"network fetch for {cacheKey}, {walk.Count} provider(s) in walk" +
            (hasPreference ? $" (preferred: {preferredProvider})" : ""));

        int total = walk.Count;
        int index = 0;

        foreach (var key in walk)
        {
            index++;
            double pct = (double)index / total * 100.0;

            var provider = GetProvider(key);
            if (provider == null)
            {
                LogManager.Error(LogTag.Data, $"provider '{key}' not registered");
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
                _memoryCache.Set(cacheKey, new MemoryEntry(fetched, provider.Name), TimeSpan.FromMinutes(10));
                await _diskCache.SaveCacheAsync(cacheKey, fetched, provider.Name);
                return fetched;
            }

            LogManager.Warn(LogTag.Data, $"{provider.Name} returned empty for {cacheKey}, falling back");
            progress?.Report(new DataProgress($"{provider.Name} returned no data. Trying next...", true, pct));
        }

        LogManager.Warn(LogTag.Data, $"all providers exhausted for {cacheKey}, no data");
        progress?.Report(new DataProgress("All providers exhausted — no data.", false, 0));
        return new List<Kline>();
    }

    /// <summary>
    /// Synchronous metadata scan of data/cache. Kept for back-compat;
    /// callers on the UI thread should prefer GetAvailableDataAsync.
    /// </summary>
    public List<DataEntry> GetAvailableData()
    {
        var files = CollectCacheFiles();
        var metas = new CacheMetadata?[files.Length];
        for (int i = 0; i < files.Length; i++)
            metas[i] = _diskCache.ReadCacheMetadata(files[i]);

        return BuildEntries(files, metas);
    }

    /// <summary>
    /// Off-thread, parallel metadata scan. Each file's ReadCacheMetadata
    /// is independent — full JSON parse + full-bar enumeration — so the
    /// Parallel.For cuts wall time by core count and Task.Run keeps the
    /// UI thread free.
    /// </summary>
    public async Task<List<DataEntry>> GetAvailableDataAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var files = CollectCacheFiles();
            var metas = new CacheMetadata?[files.Length];

            Parallel.For(
                0,
                files.Length,
                new ParallelOptions { CancellationToken = ct },
                i => metas[i] = _diskCache.ReadCacheMetadata(files[i]));

            ct.ThrowIfCancellationRequested();
            return BuildEntries(files, metas);
        }, ct).ConfigureAwait(true);
    }

    private static string[] CollectCacheFiles()
    {
        const string cacheDir = "data/cache";
        return Directory.Exists(cacheDir)
            ? Directory.GetFiles(cacheDir, "*.json")
            : Array.Empty<string>();
    }

    private List<DataEntry> BuildEntries(string[] cacheFiles, CacheMetadata?[] metas)
    {
        var entries = new List<DataEntry>(cacheFiles.Length + 16);

        for (int i = 0; i < cacheFiles.Length; i++)
        {
            var file = cacheFiles[i];
            var info = new FileInfo(file);
            var meta = metas[i];

            entries.Add(new DataEntry(
                Symbol: meta?.Symbol ?? Path.GetFileNameWithoutExtension(info.Name),
                Timeframe: meta?.Timeframe ?? "",
                Tier: "Cache",
                Provider: meta?.Source ?? "Unknown",
                Path: info.FullName,
                SizeBytes: info.Length,
                LastModifiedLocal: info.LastWriteTime,
                HasVolume: meta?.HasVolume ?? false));
        }

        if (Directory.Exists("data"))
        {
            foreach (var file in Directory.GetFiles("data", "*.csv"))
            {
                var info = new FileInfo(file);
                entries.Add(new DataEntry(
                    Symbol: Path.GetFileNameWithoutExtension(info.Name),
                    Timeframe: "",
                    Tier: "CSV",
                    Provider: "Unknown",
                    Path: info.FullName,
                    SizeBytes: info.Length,
                    LastModifiedLocal: info.LastWriteTime,
                    HasVolume: false));
            }
        }

        return entries
            .OrderBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Timeframe, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetCachedSymbols()
    {
        const string cacheDir = "data/cache";
        if (!Directory.Exists(cacheDir)) return new List<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(cacheDir, "*.json"))
        {
            var symbol = _diskCache.ReadSymbolFromCacheFile(file);
            if (!string.IsNullOrWhiteSpace(symbol))
                seen.Add(symbol);
        }

        return seen
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetCachedTimeframes(string symbol)
    {
        const string cacheDir = "data/cache";
        if (!Directory.Exists(cacheDir) || string.IsNullOrWhiteSpace(symbol))
            return new List<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(cacheDir, "*.json"))
        {
            var meta = _diskCache.ReadCacheMetadata(file);
            if (meta == null) continue;
            if (!string.Equals(meta.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(meta.Timeframe))
                seen.Add(meta.Timeframe);
        }

        var ordered = new List<string>();
        foreach (var tf in TimeframeMap.All)
            if (seen.Contains(tf))
            {
                ordered.Add(tf);
                seen.Remove(tf);
            }

        ordered.AddRange(seen.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    public (bool Cached, string Provider)? GetCacheInfo(string symbol, string timeframe)
    {
        var cacheKey = $"{symbol}@{timeframe}";
        var path = Path.Combine("data/cache",
            cacheKey.Replace('@', '_').Replace('/', '_').Replace('\\', '_') + ".json");

        if (!File.Exists(path)) return null;

        var meta = _diskCache.ReadCacheMetadata(path);
        return (true, meta?.Source ?? "Unknown");
    }
}