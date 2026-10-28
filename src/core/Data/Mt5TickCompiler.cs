using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;
using backs2.Core.Models;

namespace backs2.Core.Data;

public sealed class Mt5TickCompiler : ITickCompiler
{
    private const int MaxZeroBidSamples = 10_000;

    private readonly CacheManager _cache;

    public Mt5TickCompiler(CacheManager cache) { _cache = cache; }

    public async Task<TickCompileResult> CompileAsync(
        string csvPath,
        string timeframe,
        IProgress<TickCompileProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) throw new ArgumentException("csvPath empty", nameof(csvPath));
        if (!File.Exists(csvPath)) throw new FileNotFoundException("CSV not found", csvPath);
        if (string.IsNullOrWhiteSpace(timeframe)) throw new ArgumentException("timeframe empty", nameof(timeframe));

        var fileName = Path.GetFileNameWithoutExtension(csvPath);
        var symbol = fileName.Split('_')[0];
        var tf = timeframe;
        var path = csvPath;

        var result = await Task.Run(() => ParseCsvCore(path, tf, progress, ct), ct);
        string cacheKey = symbol + "@" + tf;

        if (result.ZeroBidTicks.Count > 0)
        {
            LogManager.Warn(LogTag.Provider,
                "MT5: dropped " + result.ZeroBidTicks.Count.ToString("N0") + " tick(s) with zero bid during parsing of " + fileName);
        }

        await _cache.SaveCacheAsync(cacheKey, result.Klines, "MT5 Parsed", DataOrigin.RawTick);

        LogManager.Info(LogTag.Provider,
            "MT5: parsed " + result.LinesRead.ToString("N0") + " ticks into " + result.Klines.Count.ToString("N0") + " bars (" + cacheKey + ")");

        return new TickCompileResult(
            Symbol: symbol,
            Timeframe: tf,
            LinesRead: result.LinesRead,
            Bars: result.Klines.Count,
            ZeroBidTicks: result.ZeroBidTicks,
            CacheKey: cacheKey);
    }

    private sealed record ParseCsvResult(
        int LinesRead,
        List<Kline> Klines,
        List<long> ZeroBidTicks);

    private static ParseCsvResult ParseCsvCore(
        string path, string timeframe, IProgress<TickCompileProgress>? progress, CancellationToken ct)
    {
        var klines = new List<Kline>();
        var zeroBidTicks = new List<long>();
        long currentBucket = -1;
        double o = 0, h = double.MinValue, l = double.MaxValue, c = 0;
        long v = 0;

        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.SequentialScan);
        using var sr = new StreamReader(fs);
        long totalLength = fs.Length;
        int linesRead = 0;

        sr.ReadLine();

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            linesRead++;
            if (linesRead % 100_000 == 0 && progress != null)
            {
                double pct = totalLength > 0
                    ? (double)fs.Position / totalLength * 100
                    : 0;
                progress.Report(new TickCompileProgress(pct, "Parsing... " + linesRead.ToString("N0") + " ticks processed"));
            }

            if (!TryParseTickLine(line, out long timeMsc, out double bid)) continue;

            if (bid <= 0)
            {
                if (zeroBidTicks.Count < MaxZeroBidSamples)
                    zeroBidTicks.Add(timeMsc);
                continue;
            }

            long bucket = GetBucketStart(timeMsc, timeframe);

            if (bucket != currentBucket)
            {
                if (currentBucket != -1)
                {
                    klines.Add(new Kline(
                        DateTimeOffset.FromUnixTimeMilliseconds(currentBucket).UtcDateTime,
                        o, h, l, c, v));
                }
                currentBucket = bucket;
                o = h = l = c = bid;
                v = 1;
            }
            else
            {
                if (bid > h) h = bid;
                if (bid < l) l = bid;
                c = bid;
                v++;
            }
        }

        if (currentBucket != -1)
        {
            klines.Add(new Kline(
                DateTimeOffset.FromUnixTimeMilliseconds(currentBucket).UtcDateTime,
                o, h, l, c, v));
        }

        return new ParseCsvResult(linesRead, klines, zeroBidTicks);
    }

    private static bool TryParseTickLine(string line, out long timeMsc, out double bid)
    {
        timeMsc = 0;
        bid = 0;

        int idx1 = line.IndexOf(',');
        if (idx1 < 0) return false;

        int idx2 = line.IndexOf(',', idx1 + 1);
        if (idx2 < 0) return false;

        var timeSpan = line.AsSpan(0, idx1);
        var bidSpan  = line.AsSpan(idx1 + 1, idx2 - idx1 - 1);

        if (!long.TryParse(timeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeMsc))
            return false;
        if (!double.TryParse(bidSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out bid))
            return false;

        return true;
    }

    private static long GetBucketStart(long timeMsc, string tf)
    {
        long tfMs = tf switch
        {
            "1m"  =>            60_000L,
            "2m"  =>           120_000L,
            "3m"  =>           180_000L,
            "5m"  =>           300_000L,
            "15m" =>           900_000L,
            "30m" =>         1_800_000L,
            "45m" =>         2_700_000L,
            "1h"  =>         3_600_000L,
            "2h"  =>         7_200_000L,
            "3h"  =>        10_800_000L,
            "4h"  =>        14_400_000L,
            "1D"  =>        86_400_000L,
            _     => 0L
        };

        if (tfMs > 0)
            return (timeMsc / tfMs) * tfMs;

        if (tf == "1W")
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(timeMsc).UtcDateTime;
            int diff = (7 + (dt.DayOfWeek - DayOfWeek.Monday)) % 7;
            var monday = dt.Date.AddDays(-diff);
            return new DateTimeOffset(monday, TimeSpan.Zero).ToUnixTimeMilliseconds();
        }

        return timeMsc;
    }
}