// src/core/CacheManager.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core;

public sealed record CacheLoadResult(List<Kline> Bars, string Source);

public sealed record CacheMetadata(string Symbol, string Timeframe, string Source, bool HasVolume, long MinT, long MaxT);

public class CacheManager
{
    private readonly string _cacheDir = "data/cache";

    /// <summary>
    /// Memoized metadata keyed on (path, last-write ticks, length).
    /// ReadCacheMetadata parses the whole JSON and enumerates every bar, so a
    /// 600k-bar 1m file costs a full parse per call — and the Data tab, the
    /// Providers tab and every refresh each call it for every file. The key
    /// includes the file stamp, so a rewritten cache invalidates itself and no
    /// explicit clear is needed anywhere.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Stamp, CacheMetadata? Meta)> _metaCache = new();

    public CacheManager()
    {
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<CacheLoadResult?> LoadCacheAsync(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;

        try
        {
            var sw = Stopwatch.StartNew();
            await using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (fs.Length == 0) return null;

            var envelope = await JsonSerializer.DeserializeAsync(
                fs,
                KlineJsonContext.Default.CacheEnvelope);

            if (envelope == null || envelope.Bars == null || envelope.Bars.Length == 0)
                return null;

            var list = new List<Kline>(envelope.Bars.Length);
            bool allZero = true;
            foreach (var c in envelope.Bars)
            {
                if (c.O != 0 || c.H != 0 || c.L != 0 || c.C != 0) allZero = false;
                list.Add(new Kline(
                    DateTimeOffset.FromUnixTimeMilliseconds(c.T).UtcDateTime,
                    c.O, c.H, c.L, c.C, c.V));
            }

            if (allZero) return null;

            sw.Stop();
            var src = string.IsNullOrWhiteSpace(envelope.Source) ? "Unknown" : envelope.Source;
            LogManager.Info(LogTag.Cache,
                $"loaded {key} from {src} ({list.Count} bars, {sw.ElapsedMilliseconds} ms)");

            return new CacheLoadResult(list, src);
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Cache, $"load failed for {key}", ex);
            return null;
        }
    }

    public async Task SaveCacheAsync(string key, List<Kline> data, string source)
    {
        if (data == null || data.Count == 0) return;

        try
        {
            var sw = Stopwatch.StartNew();
            var compact = new KlineCompact[data.Count];
            for (int i = 0; i < data.Count; i++)
            {
                var k = data[i];
                compact[i] = new KlineCompact(
                    new DateTimeOffset(k.Timestamp).ToUnixTimeMilliseconds(),
                    k.Open, k.High, k.Low, k.Close, k.Volume);
            }

            var at = key.IndexOf('@');
            var symbol = at > 0 ? key.Substring(0, at) : key;
            var timeframe = at > 0 ? key.Substring(at + 1) : "";

            var envelope = new CacheEnvelope(
                Source: source ?? "Unknown",
                FetchedUtc: DateTime.UtcNow,
                Symbol: symbol,
                Timeframe: timeframe,
                Bars: compact);

            var path = PathFor(key);
            await using var fs = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous);

            await JsonSerializer.SerializeAsync(
                fs,
                envelope,
                KlineJsonContext.Default.CacheEnvelope);

            sw.Stop();
            LogManager.Info(LogTag.Cache,
                $"saved {key} from {source} ({data.Count} bars, {sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Cache, $"save failed for {key}", ex);
        }
    }

    public async Task BenchmarkAsync(int barCount)
    {
        var synthetic = new List<Kline>(barCount);
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < barCount; i++)
        {
            synthetic.Add(new Kline(start.AddMinutes(i), 1, 1, 1, 1, 1));
        }

        var sw = Stopwatch.StartNew();
        await SaveCacheAsync("__benchmark", synthetic, "Benchmark");
        sw.Stop();
        LogManager.Info(LogTag.Cache, $"benchmark saved {barCount} bars in {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var loaded = await LoadCacheAsync("__benchmark");
        sw.Stop();
        LogManager.Info(LogTag.Cache,
            $"benchmark loaded {loaded?.Bars.Count ?? 0} bars in {sw.ElapsedMilliseconds} ms");
    }

    public CacheMetadata? ReadCacheMetadata(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var info = new FileInfo(path);
            long stamp = info.LastWriteTimeUtc.Ticks ^ info.Length;
            if (_metaCache.TryGetValue(path, out var hit) && hit.Stamp == stamp)
                return hit.Meta;

            var parsed = ParseCacheMetadata(path);
            _metaCache[path] = (stamp, parsed);
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    private CacheMetadata? ParseCacheMetadata(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var symbol = root.TryGetProperty("symbol", out var s) ? s.GetString() : null;
            var timeframe = root.TryGetProperty("timeframe", out var t) ? t.GetString() : null;
            var source = root.TryGetProperty("source", out var src) ? src.GetString() : null;

            bool hasVol = false;
            long minT = 0;
            long maxT = 0;

            if (root.TryGetProperty("bars", out var bars) && bars.ValueKind == JsonValueKind.Array)
            {
                long runningMin = long.MaxValue;
                long runningMax = long.MinValue;

                foreach (var bar in bars.EnumerateArray())
                {
                    if (!hasVol && bar.TryGetProperty("v", out var v) && v.GetInt64() > 0)
                        hasVol = true;

                    if (bar.TryGetProperty("t", out var tv))
                    {
                        long val = tv.GetInt64();
                        if (val < runningMin) runningMin = val;
                        if (val > runningMax) runningMax = val;
                    }
                }

                if (runningMin != long.MaxValue) minT = runningMin;
                if (runningMax != long.MinValue) maxT = runningMax;
            }

            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(timeframe))
                return null;

            return new CacheMetadata(
                symbol!,
                timeframe!,
                string.IsNullOrWhiteSpace(source) ? "Unknown" : source!,
                hasVol,
                minT,
                maxT);
        }
        catch
        {
            return null;
        }
    }

    public string? ReadSourceFromCacheFile(string path)
        => ReadCacheMetadata(path)?.Source;

    public string? ReadSymbolFromCacheFile(string path)
        => ReadCacheMetadata(path)?.Symbol;

    public string? ReadTimeframeFromCacheFile(string path)
        => ReadCacheMetadata(path)?.Timeframe;

    /// <summary>
    /// Drops entries from the metadata memo whose cache file no longer exists.
    /// Called after a full cache-directory scan so the memo does not grow without
    /// bound across months of fetches. Does not parse anything - File.Exists is one
    /// cheap syscall per entry.
    /// </summary>
    public void PruneMetadataCache()
    {
        foreach (var kv in _metaCache)
        {
            if (!File.Exists(kv.Key))
                _metaCache.TryRemove(kv.Key, out _);
        }
    }

    private string PathFor(string key)
    {
        var safe = key.Replace('@', '_').Replace('/', '_').Replace('\\', '_');
        return Path.Combine(_cacheDir, safe + ".json");
    }
}