// src/gui/MT5/Mt5TickViewModel.cs
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Models;
using backs2.Core.MT5;
using backs2.Core.Providers;
using backs2.Core.Writers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel : ObservableObject
{
    // Grid preview hard cap. The fetch continues to stream every tick to
    // CSV beyond this; the DataGrid simply stops growing.
    public const int MaxGridRows = 100_000;

    // ── Fetch settings ──────────────────────────────────────────────
    [ObservableProperty] private string _symbol = "GOLD.pro";
    [ObservableProperty] private int    _lookbackHours = 24;
    [ObservableProperty] private int    _targetTicks = 5000;
    [ObservableProperty] private int    _chunkSize = 20000;
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

    public string[] Timeframes => TimeframeMap.All;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingCommand))]
    [NotifyCanExecuteChangedFor(nameof(FetchTicksCommand))]
    [NotifyCanExecuteChangedFor(nameof(GetTerminalInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyRangeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyToFetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ParseCsvCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isFetching;

    // ── Verify panel state ──────────────────────────────────────────
    [ObservableProperty] private bool   _isVerifyExpanded = false;
    [ObservableProperty] private string _verifyFrom = "";
    [ObservableProperty] private string _verifyTo = "";
    [ObservableProperty] private double _verifyUtcOffsetHours = 0.0;
    [ObservableProperty] private string _verifyResolvedText = "";
    [ObservableProperty] private string _verifyStatusText = "";
    [ObservableProperty] private string _verifyComparisonText = "";
    [ObservableProperty] private IBrush _verifyComparisonBrush = Brushes.Gray;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadLastFetchRangeCommand))]
    private bool _hasLastFetch;

    private long _lastFetchFromMsc = 0;
    private long _lastFetchEndMsc  = 0;
    private int  _lastFetchCount   = 0;

    // Populated by VerifyRange; consumed by ApplyToFetch to size TargetTicks.
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
    }

    private bool CanRunIdle() => !IsBusy;

    // Auto-disable grid population when TargetTicks would clearly blow
    // past the cap. Guards against the freeze class: the user typing a
    // huge number and forgetting to uncheck the box.
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
                        // Cap-at-N semantics: append only the room left.
                        // Once full, stop appending and show the banner.
                        // The fetch itself never notices — CSV streaming
                        // continues through the channel.
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
    //
    // Correctness notes:
    //  - The whole parse runs inside Task.Run — synchronous, background
    //    thread. The entire body is CPU-bound; background-thread execution
    //    via Task.Run is a single clear boundary the UI thread can't
    //    accidentally re-enter.
    //  - Progress is marshalled via IProgress<T> — Task.Run reports on a
    //    threadpool thread, Progress<T> posts to the captured context.
    //  - GetBucketStart is epoch-relative for all intraday timeframes.
    //    The old per-hour anchoring broke 45m; see the comment on the
    //    method for details.
    //  - zeroBidTicks is capped at 10k entries so an anomalous feed
    //    cannot grow it without bound.
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

        sr.ReadLine(); // skip header

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

    // Epoch-relative alignment for every intraday timeframe. The formula
    // `(timeMsc / tfMs) * tfMs` produces buckets that always tile evenly:
    // every 45m bucket is exactly 45 minutes, and no bucket straddles the
    // hour in a way that splits it. The previous per-hour anchoring was
    // only correct for divisors of 60; 45m produced alternating 45- and
    // 15-minute bars.
    //
    //   1D → UTC midnight, epoch-relative. MT5's PERIOD_D1 uses the
    //        broker's server-day boundary (typically GMT+2/3 on OANDA),
    //        so parsed daily bars will be offset from what the MT5 chart
    //        shows. Documented limitation; not adjustable yet.
    //   1W → ISO Monday, UTC-aligned. Same broker-offset caveat.
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

        // Unknown timeframe — one bar per tick. Should never fire; the
        // ComboBox is bound to TimeframeMap.All.
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

    private bool CanStop() => IsFetching;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    // ── Verify panel ────────────────────────────────────────────────
    partial void OnVerifyFromChanged(string value) => UpdateResolvedPreview();
    partial void OnVerifyToChanged(string value) => UpdateResolvedPreview();
    partial void OnVerifyUtcOffsetHoursChanged(double value) => UpdateResolvedPreview();

    private bool CanLoadLastFetchRange() => HasLastFetch;

    [RelayCommand(CanExecute = nameof(CanLoadLastFetchRange))]
    private void LoadLastFetchRange()
    {
        if (!HasLastFetch) return;
        VerifyFrom = FormatMscForOffset(_lastFetchFromMsc, VerifyUtcOffsetHours);
        VerifyTo   = FormatMscForOffset(_lastFetchEndMsc + 1, VerifyUtcOffsetHours);
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task VerifyRange()
    {
        if (!TryParseTimestamp(VerifyFrom, VerifyUtcOffsetHours, out long fromMsc, out string errFrom))
        {
            VerifyStatusText = $"From parse error: {errFrom}";
            VerifyComparisonText = "";
            VerifyComparisonBrush = Brushes.OrangeRed;
            return;
        }
        if (!TryParseTimestamp(VerifyTo, VerifyUtcOffsetHours, out long toMsc, out string errTo))
        {
            VerifyStatusText = $"To parse error: {errTo}";
            VerifyComparisonText = "";
            VerifyComparisonBrush = Brushes.OrangeRed;
            return;
        }
        if (toMsc <= fromMsc)
        {
            VerifyStatusText = "Range is empty or inverted (To must be > From).";
            VerifyComparisonText = "";
            VerifyComparisonBrush = Brushes.OrangeRed;
            return;
        }

        VerifyStatusText = "Querying MT5...";
        VerifyComparisonText = "";

        try
        {
            var result = await _service.CountTicksAsync(Symbol, fromMsc, toMsc, CancellationToken.None);
            if (result == null)
            {
                VerifyStatusText = "No response from MT5 EA.";
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
        if (!TryParseTimestamp(VerifyFrom, VerifyUtcOffsetHours, out long fromMsc, out string errFrom))
        {
            VerifyStatusText = $"Cannot apply: From parse error: {errFrom}";
            return;
        }
        if (!TryParseTimestamp(VerifyTo, VerifyUtcOffsetHours, out long toMsc, out string errTo))
        {
            VerifyStatusText = $"Cannot apply: To parse error: {errTo}";
            return;
        }
        if (toMsc <= fromMsc)
        {
            VerifyStatusText = "Cannot apply: empty or inverted range.";
            return;
        }

        long nowMsc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (fromMsc >= nowMsc)
        {
            VerifyStatusText = "Cannot apply: From is in the future.";
            return;
        }

        long lookbackMs = nowMsc - fromMsc;
        int lookbackHours = (int)Math.Ceiling(lookbackMs / 3_600_000.0);
        if (lookbackHours <= 0) lookbackHours = 1;

        LookbackHours = lookbackHours;

        if (_lastVerifyCount > 0)
        {
            long target = (long)_lastVerifyCount + Math.Max(1000L, _lastVerifyCount / 100L);
            if (target > 50_000_000L) target = 50_000_000L;
            TargetTicks = (int)target;

            if (TargetTicks > 500_000) PopulateGrid = false;

            VerifyStatusText =
                $"Applied to fetch — Lookback = {lookbackHours} h, Target = {TargetTicks:N0}. " +
                "Symbol and batch size unchanged.";
        }
        else
        {
            VerifyStatusText =
                $"Applied to fetch — Lookback = {lookbackHours} h. " +
                "Target ticks unchanged (run Verify first to auto-size it). " +
                "Symbol and batch size unchanged.";
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────
    private void UpdateResolvedPreview()
    {
        if (string.IsNullOrWhiteSpace(VerifyFrom) && string.IsNullOrWhiteSpace(VerifyTo))
        {
            VerifyResolvedText = "";
            return;
        }

        bool okFrom = TryParseTimestamp(VerifyFrom, VerifyUtcOffsetHours, out long fromMsc, out string errFrom);
        bool okTo   = TryParseTimestamp(VerifyTo,   VerifyUtcOffsetHours, out long toMsc,   out string errTo);

        if (!okFrom || !okTo)
        {
            VerifyResolvedText = okFrom
                ? $"To: {errTo}"
                : $"From: {errFrom}";
            return;
        }

        long spanMs = toMsc - fromMsc;

        VerifyResolvedText =
            $"From:  {FormatUtc(fromMsc)}\n" +
            $"To:    {FormatUtc(toMsc)}\n" +
            (spanMs > 0 ? $"Span:  {FormatDuration(spanMs)}" : "Span:  (invalid — To must be > From)");
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

        int ms = 0;
        int dotIdx = timePart.IndexOf('.');
        string hmsPart = dotIdx < 0 ? timePart : timePart.Substring(0, dotIdx);
        if (dotIdx >= 0)
        {
            var msStr = timePart.Substring(dotIdx + 1);
            while (msStr.Length < 3) msStr += "0";
            if (msStr.Length > 3) msStr = msStr.Substring(0, 3);
            if (!int.TryParse(msStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
            { err = "ms fraction must be numeric"; return false; }
        }

        var tp = hmsPart.Split(':');
        if (tp.Length != 3) { err = "time must be HH:MM:SS[.mmm]"; return false; }

        if (!int.TryParse(tp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour) ||
            !int.TryParse(tp[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute) ||
            !int.TryParse(tp[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int second))
        { err = "time components must be numeric"; return false; }

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
        return shifted.ToString("yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
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