using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core;

public class CacheManager
{
    private readonly string _cacheDir = "data/cache";
    private readonly LogManager _logger;

    public CacheManager(LogManager logger)
    {
        _logger = logger;
        if (!Directory.Exists(_cacheDir))
            Directory.CreateDirectory(_cacheDir);
    }

    public async Task SaveCacheAsync(string symbol, List<Kline> data)
    {
        var path = Path.Combine(_cacheDir, $"{symbol}.json");

        var compact = new KlineCompact[data.Count];
        for (int i = 0; i < data.Count; i++)
        {
            var k = data[i];
            compact[i] = new KlineCompact(
                new DateTimeOffset(k.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                k.Open, k.High, k.Low, k.Close, k.Volume);
        }

        var sw = Stopwatch.StartNew();
        await using (var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                compact,
                KlineJsonContext.Default.KlineCompactArray);
        }
        sw.Stop();

        _logger.Info($"Cache: saved {symbol} ({data.Count} bars, {sw.ElapsedMilliseconds} ms)");
    }

    public async Task<List<Kline>?> LoadCacheAsync(string symbol)
    {
        var path = Path.Combine(_cacheDir, $"{symbol}.json");
        if (!File.Exists(path)) return null;

        var sw = Stopwatch.StartNew();

        KlineCompact[]? compact;
        await using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan | FileOptions.Asynchronous))
        {
            try
            {
                compact = await JsonSerializer.DeserializeAsync(
                    stream,
                    KlineJsonContext.Default.KlineCompactArray);
            }
            catch (JsonException ex)
            {
                _logger.Error($"Cache: {symbol} — malformed file, will refetch ({ex.Message})");
                return null;
            }
        }

        sw.Stop();

        if (compact == null || compact.Length == 0)
        {
            _logger.Info($"Cache: {symbol} — empty file, will refetch");
            return null;
        }

        // Reject all-zero bars (hallmark of an old-format file being read as compact).
        bool allZero = true;
        foreach (var c in compact)
        {
            if (c.T != 0 || c.O != 0 || c.H != 0 || c.L != 0 || c.C != 0)
            {
                allZero = false;
                break;
            }
        }
        if (allZero)
        {
            _logger.Info($"Cache: {symbol} — all-zero payload (legacy format), will refetch");
            return null;
        }

        var result = new List<Kline>(compact.Length);
        foreach (var c in compact)
        {
            result.Add(new Kline(
                DateTimeOffset.FromUnixTimeMilliseconds(c.T).UtcDateTime,
                c.O, c.H, c.L, c.C, c.V));
        }

        _logger.Info($"Cache: loaded {symbol} ({result.Count} bars, {sw.ElapsedMilliseconds} ms)");
        return result;
    }

    public async Task BenchmarkAsync(int barCount = 10_000)
    {
        var rng = new Random(1234);
        var data = new List<Kline>(barCount);
        var start = DateTime.UtcNow.AddDays(-barCount);

        for (int i = 0; i < barCount; i++)
        {
            var px = 4000 + rng.NextDouble() * 100;
            data.Add(new Kline(start.AddDays(i), px, px + 1, px - 1, px + 0.5, rng.Next(1000, 5000)));
        }

        const string benchSymbol = "__benchmark__";
        await SaveCacheAsync(benchSymbol, data);
        var loaded = await LoadCacheAsync(benchSymbol);

        _logger.Info($"Cache benchmark: {barCount} bars → loaded {loaded?.Count ?? 0}");

        var path = Path.Combine(_cacheDir, $"{benchSymbol}.json");
        if (File.Exists(path)) File.Delete(path);
    }
}