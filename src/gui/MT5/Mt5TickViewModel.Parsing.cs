using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel
{
    [ObservableProperty] private string _parseFilePath = "";
    [ObservableProperty] private string _parseTimeframe = "1h";
    [ObservableProperty] private double _parseProgress;
    [ObservableProperty] private string _parseStatusText = "Select a CSV to begin.";

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task ParseCsv()
    {
        if (string.IsNullOrWhiteSpace(ParseFilePath) || !File.Exists(ParseFilePath))
        {
            ParseStatusText = "File not found.";
            return;
        }

        IsBusy = true;
        ParseStatusText = "Parsing CSV...";
        ParseProgress = 0;

        try
        {
            var fileName = Path.GetFileNameWithoutExtension(ParseFilePath);
            var symbol = fileName.Split('_')[0];
            var tf = ParseTimeframe;
            var path = ParseFilePath;

            var progress = new Progress<ParseProgressReport>(p =>
            {
                ParseProgress = p.Percent;
                ParseStatusText = p.Message;
            });

            var result = await Task.Run(() => ParseCsvCore(path, tf, progress));

            if (result.ZeroBidTicks.Count > 25)
            {
                LogManager.Warn(LogTag.Provider,
                    $"MT5: dropped {result.ZeroBidTicks.Count:N0} ticks with zero bid during parsing.");
            }
            else if (result.ZeroBidTicks.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var t in result.ZeroBidTicks)
                {
                    sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime
                              .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    sb.Append(", ");
                }
                LogManager.Warn(LogTag.Provider,
                    $"MT5: dropped {result.ZeroBidTicks.Count} ticks with zero bid at: {sb.ToString().TrimEnd(',', ' ')}");
            }

            ParseProgress = 100;
            ParseStatusText = $"Saving {result.Klines.Count:N0} bars to cache...";

            string cacheKey = $"{symbol}@{tf}";
            await _cache.SaveCacheAsync(cacheKey, result.Klines, "MT5 Parsed");

            ParseStatusText = $"Success. Saved {result.Klines.Count:N0} bars for {symbol}@{tf}.";
            LogManager.Info(LogTag.Provider,
                $"MT5: parsed {result.LinesRead:N0} ticks into {result.Klines.Count:N0} bars ({symbol}@{tf})");
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5 parsing failed", ex);
            ParseStatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private sealed record ParseProgressReport(double Percent, string Message);

    private sealed record ParseCsvResult(
        int LinesRead,
        List<Kline> Klines,
        List<long> ZeroBidTicks);

    private static ParseCsvResult ParseCsvCore(
        string path, string timeframe, IProgress<ParseProgressReport> progress)
    {
        const int MaxZeroBidSamples = 10_000;

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
            linesRead++;
            if (linesRead % 100_000 == 0)
            {
                double pct = totalLength > 0
                    ? (double)fs.Position / totalLength * 100
                    : 0;
                progress.Report(new ParseProgressReport(pct, $"Parsing... {linesRead:N0} ticks processed"));
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