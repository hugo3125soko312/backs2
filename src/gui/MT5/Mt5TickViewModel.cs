// src/gui/MT5/Mt5TickViewModel.cs
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Models;
using backs2.Core.MT5;
using backs2.Core.MT5.Tkc;
using backs2.Core.Providers;
using backs2.Core.Writers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel : ObservableObject
{
    public const int MaxGridRows = 100_000;

    // ── Fetch settings ──────────────────────────────────────────────
    [ObservableProperty] private string _symbol = "GOLD.pro";
    [ObservableProperty] private int    _lookbackHours = 24;
    [ObservableProperty] private int    _targetTicks = 5000;
    [ObservableProperty] private int    _chunkSize = 50000;
    [ObservableProperty] private bool   _populateGrid = true;

    // ── Display state ───────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "Not connected.";
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private int    _latencyMs;
    [ObservableProperty] private double _fetchProgress;
    [ObservableProperty] private string _fetchProgressText = "";
    [ObservableProperty] private string _fetchEtaText = "";
    [ObservableProperty] private string _fetchSpeedText = "";
    [ObservableProperty] private string _terminalInfoText = "";
    [ObservableProperty] private string _connectionInfoText = "";
    [ObservableProperty] private string _gridCapText = "";

    // ── Parsing state ───────────────────────────────────────────────
    [ObservableProperty] private string _parseFilePath = "";
    [ObservableProperty] private string _parseTimeframe = "1h";
    [ObservableProperty] private double _parseProgress;
    [ObservableProperty] private string _parseStatusText = "Select a CSV to begin.";

    // ── .tkc extraction state ───────────────────────────────────────
    [ObservableProperty] private string _terminalDataRoot = "";
    [ObservableProperty] private string _terminalBroker = "";
    [ObservableProperty] private string _tkcSymbol = "GOLD.pro";
    [ObservableProperty] private string _tkcTimeframe = "1h";
    [ObservableProperty] private int    _tkcLookbackDays = 0;
    [ObservableProperty] private int    _tkcMaxTicks = 0;
    [ObservableProperty] private double _tkcProgress;
    [ObservableProperty] private string _tkcProgressText = "";
    [ObservableProperty] private string _tkcStatusText = "";

    public string[] Timeframes => TimeframeMap.All;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingCommand))]
    [NotifyCanExecuteChangedFor(nameof(FetchTicksCommand))]
    [NotifyCanExecuteChangedFor(nameof(GetTerminalInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyRangeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyToFetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ParseCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractToCacheCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isFetching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isDecoding;

    // ── Verify panel state ──────────────────────────────────────────
    [ObservableProperty] private bool   _isVerifyExpanded = false;
    [ObservableProperty] private int    _verifySpanValue = 30;
    [ObservableProperty] private string _verifySpanUnit = "Years";
    [ObservableProperty] private string _verifyAnchorDate = "2026/10/02 10:00:00:000";
    [ObservableProperty] private double _verifyUtcOffsetHours = 0.0;
    [ObservableProperty] private string _verifyResolvedText = "";
    [ObservableProperty] private string _verifyStatusText = "";
    [ObservableProperty] private string _verifyComparisonText = "";
    [ObservableProperty] private IBrush _verifyComparisonBrush = Brushes.Gray;

    public string[] SpanUnits { get; } = new[]
    {
        "Seconds", "Minutes", "Hours", "Days", "Weeks", "Months", "Years"
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadLastFetchRangeCommand))]
    private bool _hasLastFetch;

    private long _lastFetchFromMsc = 0;
    private long _lastFetchEndMsc  = 0;
    private int  _lastFetchCount   = 0;

    private int _lastVerifyCount = 0;

    public RangeObservableCollection<Mt5Tick> Ticks { get; } = new();

    private readonly Mt5TickService _service;
    private readonly ConfigManager _config;
    private readonly CacheManager _cache;
    private CancellationTokenSource? _cts;

    public Mt5TickViewModel(Mt5TickService service, ConfigManager config, CacheManager cache)
    {
        _service = service;
        _config = config;
        _cache = cache;
        ConnectionInfoText = $"Server listening on {_config.Settings.Mt5Host}:{_config.Settings.Mt5Port}";

        TerminalDataRoot = _config.Settings.Mt5TerminalDataRoot ?? "";
        TerminalBroker   = _config.Settings.Mt5TerminalBroker ?? "";
    }

    private bool CanRunIdle() => !IsBusy;

    partial void OnTargetTicksChanged(int value)
    {
        if (value > 500_000 && PopulateGrid)
        {
            PopulateGrid = false;
            GridCapText = $"Grid auto-disabled — target {value:N0} exceeds 500,000. CSV streaming continues.";
        }
    }

    // ── Ping ────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task Ping()
    {
        IsBusy = true;
        StatusText = "Pinging...";
        try
        {
            var result = await _service.PingAsync(CancellationToken.None);
            if (result.Success)
            {
                IsConnected = true;
                LatencyMs   = result.LatencyMs;
                StatusText  = $"Connected — {result.LatencyMs} ms";
            }
            else
            {
                IsConnected = false;
                LatencyMs   = 0;
                StatusText  = $"Disconnected — {result.Error}";
            }
        }
        finally { IsBusy = false; }
    }

    // ── Fetch ───────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task FetchTicks()
    {
        IsBusy = true;
        IsFetching = true;
        Ticks.Clear();
        FetchProgress = 0;
        FetchProgressText = "";
        FetchEtaText = "";
        FetchSpeedText = "";
        GridCapText = "";
        StatusText = "Opening stream writer...";

        _cts = new CancellationTokenSource();

        long fromMsc = LookbackHours <= 0
            ? 0
            : DateTimeOffset.UtcNow.AddHours(-LookbackHours).ToUnixTimeMilliseconds();

        Mt5TickStreamWriter? writer = null;
        Channel<IReadOnlyList<Mt5Tick>>? channel = null;
        Task? flushTask = null;

        int trackedFetched = 0;
        long trackedLastMsc = 0;

        try
        {
            writer = await Mt5TickStreamWriter.OpenAsync(Symbol);
            var localWriter = writer;

            channel = Channel.CreateBounded<IReadOnlyList<Mt5Tick>>(
                new BoundedChannelOptions(64)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true
                });
            var localChannel = channel;

            flushTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var batch in localChannel.Reader.ReadAllAsync())
                        await localWriter.AppendAsync(batch);
                }
                catch (Exception ex)
                {
                    LogManager.Error(LogTag.Provider, "MT5: CSV flusher task failed", ex);
                }
            });

            StatusText = "Fetching ticks...";

            var progress = new Progress<Mt5FetchProgress>(p =>
            {
                trackedFetched = p.Fetched;
                FetchProgress = p.Percent;
                FetchProgressText = $"{p.Fetched} / {p.Total}";
                FetchEtaText = p.EtaSeconds > 0
                    ? $"ETA {FormatDuration((long)(p.EtaSeconds * 1000))}"
                    : "ETA —";
                FetchSpeedText = p.TicksPerSecond > 0
                    ? $"{p.TicksPerSecond:F0} t/s"
                    : "";

                if (p.NewTicks != null && p.NewTicks.Count > 0)
                {
                    trackedLastMsc = p.NewTicks[p.NewTicks.Count - 1].TimeMsc;

                    if (PopulateGrid)
                    {
                        int room = MaxGridRows - Ticks.Count;
                        if (room > 0)
                        {
                            if (p.NewTicks.Count <= room)
                            {
                                Ticks.AddRange(p.NewTicks);
                            }
                            else
                            {
                                var slice = new List<Mt5Tick>(room);
                                for (int i = 0; i < room; i++) slice.Add(p.NewTicks[i]);
                                Ticks.AddRange(slice);
                            }
                        }
                        if (Ticks.Count >= MaxGridRows)
                        {
                            PopulateGrid = false;
                            GridCapText = $"Grid preview capped at {MaxGridRows:N0} rows — full data in CSV.";
                        }
                    }
                }

                StatusText = p.TotalChunks > 0
                    ? $"{p.Phase} — chunk {p.CurrentChunk}/{p.TotalChunks}"
                    : p.Phase;
            });

            var ct = _cts.Token;
            await _service.FetchTicksAsync(
                Symbol, fromMsc, TargetTicks, ChunkSize, progress, ct,
                onBatch: async batch => await localChannel.Writer.WriteAsync(batch, ct));

            localChannel.Writer.Complete();
            await flushTask;
            flushTask = null;
            await writer.CompleteAsync();

            FetchProgress = 100;
            FetchProgressText = $"{trackedFetched} / {trackedFetched}";
            FetchEtaText = "";
            FetchSpeedText = "";
            StatusText = $"Loaded {trackedFetched:N0} ticks → {writer.Path}";
            if (!PopulateGrid && trackedFetched > MaxGridRows)
                StatusText += $" (grid shows first {MaxGridRows:N0})";

            if (trackedFetched > 0 && trackedLastMsc > 0)
            {
                _lastFetchFromMsc = fromMsc;
                _lastFetchEndMsc  = trackedLastMsc;
                _lastFetchCount   = trackedFetched;
                HasLastFetch = true;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Fetch cancelled.";
            await DrainAndCloseAsync(channel, flushTask, writer);
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: fetch failed", ex);
            StatusText = $"Error: {ex.Message}";
            await DrainAndCloseAsync(channel, flushTask, writer);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsFetching = false;
            IsBusy = false;
        }
    }

    private static async Task DrainAndCloseAsync(
        Channel<IReadOnlyList<Mt5Tick>>? channel,
        Task? flushTask,
        Mt5TickStreamWriter? writer)
    {
        try { channel?.Writer.TryComplete(); } catch { }
        if (flushTask != null)
        {
            try { await flushTask; } catch { }
        }
        if (writer != null)
        {
            try { await writer.CompleteAsync(); } catch { }
        }
    }

    // ── Parse CSV ───────────────────────────────────────────────────
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

    // ── Terminal info ───────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task GetTerminalInfo()
    {
        IsBusy = true;
        TerminalInfoText = "Loading...";
        try
        {
            var info = await _service.GetTerminalInfoAsync(CancellationToken.None);
            if (info == null)
            {
                TerminalInfoText = "Failed to retrieve terminal info.";
                return;
            }

            TerminalInfoText =
                $"Company:     {info.Company}\n" +
                $"Account:     {info.Account}\n" +
                $"Server:      {info.Server}\n" +
                $"Currency:    {info.Currency}\n" +
                $"Leverage:    1:{info.Leverage}\n" +
                $"Server time: {info.ServerTimeUtc:yyyy-MM-dd HH:mm:ss} UTC";
        }
        finally { IsBusy = false; }
    }

    // ── Save CSV ────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task SaveCsv()
    {
        if (Ticks.Count == 0)
        {
            StatusText = "No ticks to save.";
            return;
        }

        IsBusy = true;
        try
        {
            var path = await Mt5TickWriter.WriteAsync(Symbol, Ticks);
            StatusText = $"Saved {Ticks.Count} ticks to {path}";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private bool CanStop() => IsFetching || IsDecoding;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    // ── .tkc extract → cache ────────────────────────────────────────
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

        // Persist for next launch.
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

    /// <summary>
    /// True if the .tkc file's month is at or after the cutoff month. Files
    /// earlier than the cutoff contain only ticks that would be filtered
    /// out, so we skip them entirely without decoding. Non-standard filenames
    /// (anything but YYYYMM.tkc) are conservatively included.
    /// </summary>
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

    /// <summary>
    /// Decodes .tkc files newest-first, folding ticks into a bucket
    /// accumulator. Reverse iteration lets us stop as soon as either the
    /// lookback cutoff or the tick-count limit is reached, without holding
    /// all ticks in memory. Buckets track Open/Close correctly under reverse
    /// iteration: the first bid seen in a bucket is the latest tick
    /// (Close), the last bid seen is the earliest (Open).
    /// </summary>
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

        // Materialize chronological klines.
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

    // ── Verify panel ────────────────────────────────────────────────
    partial void OnVerifySpanValueChanged(int value)          => UpdateResolvedPreview();
    partial void OnVerifySpanUnitChanged(string value)        => UpdateResolvedPreview();
    partial void OnVerifyAnchorDateChanged(string value)      => UpdateResolvedPreview();
    partial void OnVerifyUtcOffsetHoursChanged(double value)  => UpdateResolvedPreview();

    private bool CanLoadLastFetchRange() => HasLastFetch;

    [RelayCommand(CanExecute = nameof(CanLoadLastFetchRange))]
    private void LoadLastFetchRange()
    {
        if (!HasLastFetch) return;

        VerifyAnchorDate = FormatMscForOffset(_lastFetchEndMsc + 1, VerifyUtcOffsetHours);

        long durMs  = _lastFetchEndMsc - _lastFetchFromMsc;
        long durSec = Math.Max(1L, durMs / 1000L);

        int val;
        string unit;

        if      (durSec % (365L * 86_400L) == 0) { val = (int)(durSec / (365L * 86_400L)); unit = "Years";   }
        else if (durSec % ( 30L * 86_400L) == 0) { val = (int)(durSec / ( 30L * 86_400L)); unit = "Months";  }
        else if (durSec % (  7L * 86_400L) == 0) { val = (int)(durSec / (  7L * 86_400L)); unit = "Weeks";   }
        else if (durSec %         86_400L  == 0) { val = (int)(durSec /         86_400L);  unit = "Days";    }
        else if (durSec %          3_600L  == 0) { val = (int)(durSec /          3_600L);  unit = "Hours";   }
        else if (durSec %             60L  == 0) { val = (int)(durSec /             60L);  unit = "Minutes"; }
        else                                     { val = (int)durSec;                      unit = "Seconds"; }

        if (val < 1) val = 1;
        VerifySpanValue = val;
        VerifySpanUnit  = unit;
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task VerifyRange()
    {
        if (!TryComputeRange(
                VerifyAnchorDate, VerifySpanValue, VerifySpanUnit, VerifyUtcOffsetHours,
                out long fromMsc, out long toMsc, out string err))
        {
            VerifyStatusText = $"Range error: {err}";
            VerifyComparisonText = "";
            VerifyComparisonBrush = Brushes.OrangeRed;
            return;
        }

        VerifyStatusText = "Querying MT5...";
        VerifyComparisonText = "";

        var progress = new Progress<Mt5CountProgress>(p =>
        {
            VerifyStatusText =
                $"Counting… chunk {p.ChunksDone}/{p.ChunksTotal} " +
                $"({p.CumulativeTicks:N0} ticks)";
        });

        try
        {
            var result = await _service.CountTicksAsync(
                Symbol, fromMsc, toMsc, progress, CancellationToken.None);

            if (result == null)
            {
                VerifyStatusText = "No response from MT5 EA (range not available or EA offline).";
                VerifyComparisonBrush = Brushes.OrangeRed;
                return;
            }

            _lastVerifyCount = result.Count;

            var sb = new StringBuilder();
            sb.AppendLine($"MT5 count:  {result.Count:N0}");

            long firstDelta = result.FirstMsc - fromMsc;
            long lastDelta  = toMsc - result.LastMsc;
            sb.AppendLine($"First tick: {FormatUtc(result.FirstMsc)}  (+{FormatDuration(firstDelta)} into range)");
            sb.AppendLine($"Last tick:  {FormatUtc(result.LastMsc)}  ({FormatDuration(lastDelta)} before end)");

            if (HasLastFetch)
            {
                if (result.Count == _lastFetchCount)
                {
                    sb.AppendLine($"✓ MATCH — last fetch returned {_lastFetchCount:N0} ticks");
                    VerifyComparisonBrush = Brushes.LimeGreen;
                }
                else
                {
                    long diff = (long)result.Count - _lastFetchCount;
                    string sign = diff > 0 ? "+" : "";
                    sb.AppendLine($"✗ MISMATCH — last fetch returned {_lastFetchCount:N0}, delta {sign}{diff:N0}");
                    VerifyComparisonBrush = Brushes.OrangeRed;
                }
            }
            else
            {
                sb.AppendLine("(no fetch to compare against — run a fetch first)");
                VerifyComparisonBrush = Brushes.Gray;
            }

            VerifyComparisonText = sb.ToString().TrimEnd();
            VerifyStatusText = $"Queried in {result.PerfUs / 1000.0:F1} ms";
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: verify range failed", ex);
            VerifyStatusText = $"Error: {ex.Message}";
            VerifyComparisonBrush = Brushes.OrangeRed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private void ApplyToFetch()
    {
        if (!TryComputeRange(
                VerifyAnchorDate, VerifySpanValue, VerifySpanUnit, VerifyUtcOffsetHours,
                out long fromMsc, out long toMsc, out string err))
        {
            VerifyStatusText = $"Cannot apply: {err}";
            return;
        }

        long nowMsc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (fromMsc >= nowMsc)
        {
            VerifyStatusText = "Cannot apply: computed From is in the future.";
            return;
        }

        long lookbackMs = nowMsc - fromMsc;
        int lookbackHours = (int)Math.Ceiling(lookbackMs / 3_600_000.0);
        if (lookbackHours <= 0) lookbackHours = 1;

        LookbackHours = lookbackHours;

        if (_lastVerifyCount > 0)
        {
            long target = (long)_lastVerifyCount + Math.Max(1000L, _lastVerifyCount / 100L);
            if (target > int.MaxValue) target = int.MaxValue;
            TargetTicks = (int)target;

            if (TargetTicks > 500_000) PopulateGrid = false;

            VerifyStatusText =
                $"Applied — Lookback = {lookbackHours:N0} h, Target = {TargetTicks:N0}. " +
                "Symbol and batch size unchanged.";
        }
        else
        {
            VerifyStatusText =
                $"Applied Lookback = {lookbackHours:N0} h. " +
                "Target ticks UNCHANGED — run Verify first to auto-size it. " +
                "If Verify returned 4401 the broker has no data in this range.";
        }
    }

    // ── Range math ──────────────────────────────────────────────────
    private static bool TryComputeRange(
        string anchorStr, int spanValue, string spanUnit, double utcOffsetHours,
        out long fromMsc, out long toMsc, out string err)
    {
        fromMsc = 0;
        toMsc   = 0;
        err     = "";

        if (!TryParseTimestamp(anchorStr, utcOffsetHours, out toMsc, out err))
            return false;

        if (spanValue <= 0)
        {
            err = "span value must be greater than zero";
            return false;
        }

        DateTimeOffset anchor =
            DateTimeOffset.FromUnixTimeMilliseconds(toMsc)
                          .ToOffset(TimeSpan.FromHours(utcOffsetHours));

        DateTimeOffset from;
        try
        {
            from = spanUnit switch
            {
                "Seconds" => anchor.AddSeconds(-spanValue),
                "Minutes" => anchor.AddMinutes(-spanValue),
                "Hours"   => anchor.AddHours  (-spanValue),
                "Days"    => anchor.AddDays   (-spanValue),
                "Weeks"   => anchor.AddDays   (-(long)spanValue * 7),
                "Months"  => anchor.AddMonths (-spanValue),
                "Years"   => anchor.AddYears  (-spanValue),
                _         => anchor
            };
        }
        catch (Exception ex)
        {
            err = $"cannot subtract {spanValue} {spanUnit}: {ex.Message}";
            return false;
        }

        fromMsc = from.ToUnixTimeMilliseconds();
        if (toMsc <= fromMsc)
        {
            err = "computed range is empty or inverted";
            return false;
        }

        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────
    private void UpdateResolvedPreview()
    {
        if (string.IsNullOrWhiteSpace(VerifyAnchorDate))
        {
            VerifyResolvedText = "";
            return;
        }

        if (!TryComputeRange(
                VerifyAnchorDate, VerifySpanValue, VerifySpanUnit, VerifyUtcOffsetHours,
                out long fromMsc, out long toMsc, out string err))
        {
            VerifyResolvedText = $"Invalid: {err}";
            return;
        }

        VerifyResolvedText =
            $"From:  {FormatUtc(fromMsc)}\n" +
            $"To:    {FormatUtc(toMsc)}\n" +
            $"Span:  {FormatDuration(toMsc - fromMsc)}";
    }

    private static bool TryParseTimestamp(string s, double utcOffsetHours, out long msc, out string err)
    {
        msc = 0;
        err = "";

        if (string.IsNullOrWhiteSpace(s)) { err = "empty"; return false; }

        var trimmed = s.Trim();
        int spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx < 0) { err = "missing space between date and time"; return false; }

        var datePart = trimmed.Substring(0, spaceIdx).Replace('-', '/').Replace('.', '/');
        var timePart = trimmed.Substring(spaceIdx + 1).Trim();

        var dp = datePart.Split('/');
        if (dp.Length != 3) { err = "date must be YYYY/MM/DD"; return false; }

        if (!int.TryParse(dp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(dp[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int month) ||
            !int.TryParse(dp[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int day))
        { err = "date components must be numeric"; return false; }

        var tp = timePart.Split(new[] { ':', '.' }, StringSplitOptions.None);
        if (tp.Length < 3 || tp.Length > 4)
        { err = "time must be HH:MM:SS or HH:MM:SS[.:]mmm"; return false; }

        if (!int.TryParse(tp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour) ||
            !int.TryParse(tp[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute) ||
            !int.TryParse(tp[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int second))
        { err = "time components must be numeric"; return false; }

        int ms = 0;
        if (tp.Length == 4)
        {
            var msStr = tp[3];
            while (msStr.Length < 3) msStr += "0";
            if (msStr.Length > 3) msStr = msStr.Substring(0, 3);
            if (!int.TryParse(msStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
            { err = "ms fraction must be numeric"; return false; }
        }

        if (year < 1970 || year > 9999) { err = "year out of range"; return false; }
        if (month < 1 || month > 12)    { err = "month out of range"; return false; }
        if (day < 1 || day > 31)        { err = "day out of range"; return false; }
        if (hour < 0 || hour > 23)      { err = "hour out of range"; return false; }
        if (minute < 0 || minute > 59)  { err = "minute out of range"; return false; }
        if (second < 0 || second > 59)  { err = "second out of range"; return false; }

        try
        {
            var dt  = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            var dto = new DateTimeOffset(dt, TimeSpan.FromHours(utcOffsetHours));
            msc = dto.ToUnixTimeMilliseconds() + ms;
            return true;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return false;
        }
    }

    private static string FormatUtc(long msc)
        => DateTimeOffset.FromUnixTimeMilliseconds(msc)
           .UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC";

    private static string FormatMscForOffset(long msc, double offsetHours)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(msc);
        var shifted = dto.ToOffset(TimeSpan.FromHours(offsetHours));
        return shifted.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)
               + ":" + shifted.Millisecond.ToString("D3", CultureInfo.InvariantCulture);
    }

    private static string FormatDuration(long ms)
    {
        if (ms < 0) return "-" + FormatDuration(-ms);
        if (ms < 1000) return $"{ms} ms";

        long totalSec = ms / 1000;
        long fracMs   = ms % 1000;
        long d = totalSec / 86400; totalSec %= 86400;
        long h = totalSec / 3600;  totalSec %= 3600;
        long m = totalSec / 60;    totalSec %= 60;
        long s = totalSec;

        var parts = new List<string>(4);
        if (d > 0) parts.Add($"{d} d");
        if (h > 0) parts.Add($"{h} h");
        if (m > 0) parts.Add($"{m} m");
        if (s > 0 || fracMs > 0 || parts.Count == 0)
            parts.Add(fracMs > 0 ? $"{s}.{fracMs:D3} s" : $"{s} s");

        return string.Join(" ", parts);
    }
}