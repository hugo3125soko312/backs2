using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.MT5;
using backs2.Core.Writers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel
{
    // ── Fetch settings ──────────────────────────────────────────────
    [ObservableProperty] private string _symbol = "GOLD.pro";
    [ObservableProperty] private int    _lookbackHours = 24;
    [ObservableProperty] private int    _targetTicks = 5000;
    [ObservableProperty] private int    _chunkSize = 50000;
    [ObservableProperty] private bool   _populateGrid = true;

    // ── Display state ───────────────────────────────────────────────
    [ObservableProperty] private double _fetchProgress;
    [ObservableProperty] private string _fetchProgressText = "";
    [ObservableProperty] private string _fetchEtaText = "";
    [ObservableProperty] private string _fetchSpeedText = "";
    [ObservableProperty] private string _terminalInfoText = "";
    [ObservableProperty] private string _gridCapText = "";

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
    private int  _lastVerifyCount  = 0;

    partial void OnTargetTicksChanged(int value)
    {
        if (value > 500_000 && PopulateGrid)
        {
            PopulateGrid = false;
            GridCapText = $"Grid auto-disabled — target {value:N0} exceeds 500,000. CSV streaming continues.";
        }
    }

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

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task GetTerminalInfo()
    {
        IsBusy = true;
        TerminalInfoText = "Loading...";
        try
        {
            var json = await _service.GetTerminalInfoAsync(CancellationToken.None);
            if (string.IsNullOrEmpty(json))
            {
                TerminalInfoText = "Failed to retrieve terminal info.";
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string company  = root.TryGetProperty("company", out var c) ? c.GetString() ?? "" : "";
            long account    = root.TryGetProperty("account", out var a) ? a.GetInt64() : 0;
            string server   = root.TryGetProperty("server", out var s) ? s.GetString() ?? "" : "";
            string currency = root.TryGetProperty("currency", out var cr) ? cr.GetString() ?? "" : "";
            long leverage   = root.TryGetProperty("leverage", out var lev) ? lev.GetInt64() : 0;
            long serverTime = root.TryGetProperty("server_time", out var st) ? st.GetInt64() : 0;

            var serverUtc = DateTimeOffset.FromUnixTimeSeconds(serverTime).UtcDateTime;

            TerminalInfoText =
                $"Company:     {company}\n" +
                $"Account:     {account}\n" +
                $"Server:      {server}\n" +
                $"Currency:    {currency}\n" +
                $"Leverage:    1:{leverage}\n" +
                $"Server time: {serverUtc:yyyy-MM-dd HH:mm:ss} UTC";
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: Parse terminal info failed", ex);
            TerminalInfoText = "Error parsing terminal info.";
        }
        finally { IsBusy = false; }
    }

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