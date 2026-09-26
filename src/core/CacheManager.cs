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

    public CacheManager()
    {
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<List<Kline>?> LoadCacheAsync(string key)
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

            var compact = await JsonSerializer.DeserializeAsync(
                fs,
                KlineJsonContext.Default.KlineCompactArray);

            if (compact == null || compact.Length == 0) return null;

            var list = new List<Kline>(compact.Length);
            bool allZero = true;
            foreach (var c in compact)
            {
                if (c.O != 0 || c.H != 0 || c.L != 0 || c.C != 0) allZero = false;
                list.Add(new Kline(
                    DateTimeOffset.FromUnixTimeMilliseconds(c.T).UtcDateTime,
                    c.O, c.H, c.L, c.C, c.V));
            }

            if (allZero) return null;

            sw.Stop();
            LogManager.Info(LogTag.Cache, $"loaded {key} ({list.Count} bars, {sw.ElapsedMilliseconds} ms)");
            return list;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Cache, $"load failed for {key}", ex);
            return null;
        }
    }

    public async Task SaveCacheAsync(string key, List<Kline> data)
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
                compact,
                KlineJsonContext.Default.KlineCompactArray);

            sw.Stop();
            LogManager.Info(LogTag.Cache, $"saved {key} ({data.Count} bars, {sw.ElapsedMilliseconds} ms)");
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
        await SaveCacheAsync("__benchmark", synthetic);
        sw.Stop();
        LogManager.Info(LogTag.Cache, $"benchmark saved {barCount} bars in {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var loaded = await LoadCacheAsync("__benchmark");
        sw.Stop();
        LogManager.Info(LogTag.Cache, $"benchmark loaded {loaded?.Count ?? 0} bars in {sw.ElapsedMilliseconds} ms");
    }

    private string PathFor(string key)
    {
        var safe = key.Replace('@', '_').Replace('/', '_').Replace('\\', '_');
        return Path.Combine(_cacheDir, safe + ".json");
    }
}