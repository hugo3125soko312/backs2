using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Models;
using backs2.Core.MT5.Tkc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel
{
    [ObservableProperty] private string _terminalDataRoot = "";
    [ObservableProperty] private string _terminalBroker = "";
    [ObservableProperty] private string _tkcSymbol = "GOLD.pro";
    [ObservableProperty] private string _tkcTimeframe = "1h";
    [ObservableProperty] private int    _tkcLookbackDays = 0;
    [ObservableProperty] private int    _tkcMaxTicks = 0;
    [ObservableProperty] private double _tkcProgress;
    [ObservableProperty] private string _tkcProgressText = "";
    [ObservableProperty] private string _tkcStatusText = "";

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task ExtractToCache()
    {
        string root   = TerminalDataRoot?.Trim() ?? "";
        string broker = TerminalBroker?.Trim() ?? "";
        string symbol = TkcSymbol?.Trim() ?? "";
        string tf     = TkcTimeframe;

        if (root.Length == 0 || broker.Length == 0 || symbol.Length == 0)
        {
            TkcStatusText = "Fill Terminal Data Root, Broker, and Symbol.";
            return;
        }

        _config.Settings.Mt5TerminalDataRoot = root;
        _config.Settings.Mt5TerminalBroker   = broker;
        _config.SaveConfig();

        var files = TkcRepository.ListSymbolFiles(root, broker, symbol);
        if (files.Count == 0)
        {
            TkcStatusText = $"No .tkc files found under {root}\\bases\\{broker}\\ticks\\{symbol}";
            return;
        }

        long? cutoffMsc = TkcLookbackDays > 0
            ? DateTimeOffset.UtcNow.AddDays(-TkcLookbackDays).ToUnixTimeMilliseconds()
            : null;

        var inRange = new List<string>(files.Count);
        if (cutoffMsc.HasValue)
        {
            foreach (var f in files)
                if (FileOverlapsLookback(f, cutoffMsc.Value))
                    inRange.Add(f);
        }
        else
        {
            inRange.AddRange(files);
        }

        if (inRange.Count == 0)
        {
            TkcStatusText = "No .tkc files fall inside the requested lookback window.";
            return;
        }

        IsBusy = true;
        IsDecoding = true;
        TkcProgress = 0;
        TkcProgressText = "Starting...";
        TkcStatusText = "";

        _cts = new CancellationTokenSource();

        try
        {
            var ct = _cts.Token;
            int maxTicks = TkcMaxTicks;

            var progress = new Progress<DecodeProgressReport>(p =>
            {
                TkcProgress = p.Percent;
                TkcProgressText = p.Message;
            });

            var result = await Task.Run(
                () => ExtractTkcCore(inRange, tf, cutoffMsc, maxTicks, progress, ct), ct);

            ct.ThrowIfCancellationRequested();

            TkcProgress = 100;
            TkcProgressText = $"Saving {result.Klines.Count:N0} bars...";

            string cacheKey = $"{symbol}@{tf}";
            await _cache.SaveCacheAsync(cacheKey, result.Klines, "MT5 Parsed");

            TkcProgressText = "";
            TkcStatusText =
                $"Success. {result.TickCount:N0} ticks → {result.Klines.Count:N0} bars " +
                $"({cacheKey}, source MT5 Parsed).";

            LogManager.Info(LogTag.Provider,
                $"MT5: .tkc extracted {result.TickCount:N0} ticks into {result.Klines.Count:N0} bars ({cacheKey})");
        }
        catch (OperationCanceledException)
        {
            TkcStatusText = "Extract cancelled.";
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: .tkc extract failed", ex);
            TkcStatusText = $"Error: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsDecoding = false;
            IsBusy = false;
        }
    }

    private sealed record DecodeProgressReport(double Percent, string Message);

    private sealed record DecodeResult(int TickCount, List<Kline> Klines);

    private static bool FileOverlapsLookback(string path, long cutoffMsc)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (stem.Length != 6) return true;
        if (!int.TryParse(stem.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return true;
        if (!int.TryParse(stem.AsSpan(4, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)) return true;
        if (m < 1 || m > 12) return true;
        if (y < 1970 || y > 9999) return true;

        var firstOfNextMonth = new DateTimeOffset(y, m, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        long fileEndMsc = firstOfNextMonth.ToUnixTimeMilliseconds();
        return fileEndMsc > cutoffMsc;
    }

    private static DecodeResult ExtractTkcCore(
        IReadOnlyList<string> files,
        string timeframe,
        long? cutoffMsc,
        int maxTicks,
        IProgress<DecodeProgressReport> progress,
        CancellationToken ct)
    {
        var buckets = new Dictionary<long, TkcBucket>(capacity: 8192);
        long totalTicks = 0;
        long droppedZeroBid = 0;
        long tickLimit = maxTicks > 0 ? maxTicks : long.MaxValue;

        int fileCount = files.Count;
        for (int fi = fileCount - 1; fi >= 0; fi--)
        {
            ct.ThrowIfCancellationRequested();

            string path = files[fi];
            double pct = (double)(fileCount - 1 - fi) / fileCount * 100.0;
            progress.Report(new DecodeProgressReport(
                pct,
                $"Decoding {Path.GetFileName(path)} ({fileCount - fi}/{fileCount})..."));

            IReadOnlyList<TkcTick> ticks;
            try
            {
                var tkc = new TkcFile(path);
                ticks = tkc.Ticks;
            }
            catch (Exception ex)
            {
                LogManager.Warn(LogTag.Provider,
                    $"MT5: skipped '{Path.GetFileName(path)}' during decode: {ex.Message}");
                continue;
            }

            bool stop = false;
            for (int k = ticks.Count - 1; k >= 0; k--)
            {
                ct.ThrowIfCancellationRequested();

                var t = ticks[k];

                if (cutoffMsc.HasValue && t.TimestampMs < cutoffMsc.Value)
                {
                    stop = true;
                    break;
                }

                if (t.Bid <= 0.0)
                {
                    droppedZeroBid++;
                    continue;
                }

                long key = GetBucketStart(t.TimestampMs, timeframe);
                if (buckets.TryGetValue(key, out var b))
                {
                    b.Open = t.Bid;
                    if (t.Bid > b.High) b.High = t.Bid;
                    if (t.Bid < b.Low)  b.Low  = t.Bid;
                    b.Volume++;
                }
                else
                {
                    buckets[key] = new TkcBucket
                    {
                        Open   = t.Bid,
                        High   = t.Bid,
                        Low    = t.Bid,
                        Close  = t.Bid,
                        Volume = 1
                    };
                }

                totalTicks++;

                if (totalTicks >= tickLimit)
                {
                    stop = true;
                    break;
                }
            }

            if (stop) break;
        }

        ct.ThrowIfCancellationRequested();

        var keys = new List<long>(buckets.Keys);
        keys.Sort();

        var klines = new List<Kline>(keys.Count);
        foreach (var key in keys)
        {
            var b = buckets[key];
            klines.Add(new Kline(
                DateTimeOffset.FromUnixTimeMilliseconds(key).UtcDateTime,
                b.Open, b.High, b.Low, b.Close, b.Volume));
        }

        if (droppedZeroBid > 0)
        {
            LogManager.Warn(LogTag.Provider,
                $"MT5: dropped {droppedZeroBid:N0} zero-bid ticks during .tkc decode.");
        }

        int reportedTicks = totalTicks > int.MaxValue ? int.MaxValue : (int)totalTicks;
        return new DecodeResult(reportedTicks, klines);
    }

    private sealed class TkcBucket
    {
        public double Open;
        public double High;
        public double Low;
        public double Close;
        public long   Volume;
    }
}