using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.MT5;



public class Mt5TickService
{
    public event Action<Mt5CountProgress>? CountProgress
    {
        add => _server.CountProgress += value;
        remove => _server.CountProgress -= value;
    }
    

    private const long DefaultCountChunkMs = 6L * 60 * 60 * 1000;

    private readonly ConfigManager _config;
    private readonly Mt5TickServer _server;

    private readonly HashSet<string> _warmedSymbols = new(StringComparer.Ordinal);
    private readonly object _warmedLock = new();

    private long _lastEmittedTimeMsc = -1;
    private int  _lastEmittedTrailing = 0;

    public bool     HasHeartbeat      => _server.HasHeartbeat;
    public TimeSpan LastHeartbeatAge  => _server.LastHeartbeatAge;

    public Mt5TickService(ConfigManager config, Mt5TickServer server)
    {
        _config = config;
        _server = server;
    }

    public async Task<Mt5PingResult> PingAsync(CancellationToken ct)
    {
        if (!_server.IsClientConnected)
            return new Mt5PingResult(false, 0, "MT5 EA not connected");

        var sw = Stopwatch.StartNew();
        var response = await _server.SendRequestAsync("PING", "", _config.Settings.Mt5TimeoutMs, ct);
        sw.Stop();

        if (response == null)
            return new Mt5PingResult(false, 0, "No response / timeout");

        try
        {
            using var doc = JsonDocument.Parse(response);
            var status = doc.RootElement.GetProperty("status").GetString();
            return status == "OK"
                ? new Mt5PingResult(true, (int)sw.ElapsedMilliseconds)
                : new Mt5PingResult(false, 0, status ?? "Unknown");
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: Ping response parse failed", ex);
            return new Mt5PingResult(false, 0, ex.Message);
        }
    }

    public async Task<string?> GetTerminalInfoAsync(CancellationToken ct)
    {
        if (!_server.IsClientConnected) return null;

        try
        {
            var rawBytes = await _server.SendRequestAsync("GET_TERMINAL_INFO", "", _config.Settings.Mt5TimeoutMs, ct);
            return rawBytes != null ? System.Text.Encoding.UTF8.GetString(rawBytes) : null;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: GET_TERMINAL_INFO failed", ex);
            return null;
        }
    }

    public async Task PrewarmAsync(string symbol, long fromMsc, CancellationToken ct)
    {
        string key = symbol?.Trim() ?? "";
        if (key.Length == 0) return;

        lock (_warmedLock)
        {
            if (!_warmedSymbols.Add(key)) return;
        }

        if (!_server.IsClientConnected) return;

        string extra = $"\"symbol\":\"{key}\",\"from_msc\":{fromMsc},\"count\":500";
        var sw = Stopwatch.StartNew();
        var response = await _server.SendRequestAsync(
            "PREWARM", extra, _config.Settings.Mt5TimeoutMs, ct);
        sw.Stop();

        if (response == null)
        {
            LogManager.Warn(LogTag.Provider,
                $"MT5: prewarm '{key}' — no response after {sw.ElapsedMilliseconds}ms");
            lock (_warmedLock) { _warmedSymbols.Remove(key); }
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            long warmUs = root.TryGetProperty("warm_us", out var w) ? w.GetInt64() : 0;
            int warmCount = root.TryGetProperty("warm_count", out var c) ? c.GetInt32() : 0;
            LogManager.Info(LogTag.Provider,
                $"MT5: prewarm '{key}' — {warmCount} ticks in {warmUs / 1000.0:F1}ms (wall {sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            LogManager.Warn(LogTag.Provider, $"MT5: prewarm '{key}' response parse failed: {ex.Message}");
        }
    }

    public async Task<Mt5SyncResult?> SyncTicksRangeAsync(
        string symbol,
        long fromMsc,
        long toMsc,
        IProgress<Mt5CountProgress>? progress,
        CancellationToken ct)
    {
        string key = symbol?.Trim() ?? "";
        if (key.Length == 0) return null;
        if (toMsc <= fromMsc) return null;
        if (!_server.IsClientConnected) return null;

        long spanDays = (toMsc - fromMsc + 86_399_999L) / 86_400_000L;
        long extraMs = Math.Min(spanDays, 60L) * 2000L;
        int requestTimeoutMs = _config.Settings.Mt5TimeoutMs + (int)extraMs;

        string extra = $"\"symbol\":\"{key}\",\"from_msc\":{fromMsc},\"to_msc\":{toMsc}";

        Action<Mt5CountProgress> handler = p => progress?.Report(p);
        _server.CountProgress += handler;

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _server.SendRequestAsync(
                "SYNC_TICKS_RANGE", extra, requestTimeoutMs, ct);
            sw.Stop();

            if (response == null)
            {
                string hb = _server.HasHeartbeat
                    ? $"{_server.LastHeartbeatAge.TotalSeconds:F1}s ago"
                    : "never";
                LogManager.Warn(LogTag.Provider,
                    $"MT5: SYNC_TICKS_RANGE '{key}' — no response after {sw.ElapsedMilliseconds}ms " +
                    $"(timeout was {requestTimeoutMs}ms, last heartbeat: {hb})");
                return null;
            }

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (status != "OK")
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : status;
                LogManager.Warn(LogTag.Provider,
                    $"MT5: SYNC_TICKS_RANGE '{key}' — EA returned status='{status}' message='{msg}'");
                return null;
            }

            int synced  = root.TryGetProperty("synced_days",  out var sd) ? sd.GetInt32() : 0;
            int skipped = root.TryGetProperty("skipped_days", out var kd) ? kd.GetInt32() : 0;
            int total   = root.TryGetProperty("total_days",   out var td) ? td.GetInt32() : 0;

            LogManager.Info(LogTag.Provider,
                $"MT5: SYNC_TICKS_RANGE '{key}' [{fromMsc}..{toMsc}) — {synced} synced, " +
                $"{skipped} skipped, {total} total day(s) in {sw.ElapsedMilliseconds}ms");

            return new Mt5SyncResult(synced, skipped, total);
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: SYNC_TICKS_RANGE parse failed", ex);
            return null;
        }
        finally
        {
            _server.CountProgress -= handler;
        }
    }

    public async Task<Mt5CountResult?> CountTicksAsync(
        string symbol,
        long fromMsc,
        long toMsc,
        IProgress<Mt5CountProgress>? progress,
        CancellationToken ct)
    {
        string key = symbol?.Trim() ?? "";
        if (key.Length == 0) return null;
        if (toMsc <= fromMsc) return null;
        if (!_server.IsClientConnected) return null;

        long spanMs = toMsc - fromMsc;
        long spanDaysCeil = (spanMs + 86_399_999L) / 86_400_000L;
        long extraMs = Math.Min(spanDaysCeil, 90L) * 5000L;
        int adaptiveTimeoutMs = _config.Settings.Mt5TimeoutMs + (int)extraMs;

        string extra = $"\"symbol\":\"{key}\",\"from_msc\":{fromMsc},\"to_msc\":{toMsc}," +
                       $"\"chunk_ms\":{DefaultCountChunkMs}";

        Action<Mt5CountProgress> handler = p => progress?.Report(p);
        _server.CountProgress += handler;

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _server.SendRequestAsync(
                "COUNT_TICKS", extra, adaptiveTimeoutMs, ct);
            sw.Stop();

            if (response == null)
            {
                string hb = _server.HasHeartbeat
                    ? $"{_server.LastHeartbeatAge.TotalSeconds:F1}s ago"
                    : "never";
                LogManager.Warn(LogTag.Provider,
                    $"MT5: COUNT_TICKS '{key}' — no response after {sw.ElapsedMilliseconds}ms " +
                    $"(timeout was {adaptiveTimeoutMs}ms, last heartbeat: {hb})");
                return null;
            }

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (status != "OK")
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : status;

                bool historyNotFound = msg != null && msg.Contains("4401");
                if (historyNotFound)
                {
                    LogManager.Info(LogTag.Provider,
                        $"MT5: COUNT_TICKS '{key}' — range not available on broker " +
                        $"(CopyTicksRange 4401). Requested [{fromMsc}..{toMsc}).");
                }
                else
                {
                    LogManager.Warn(LogTag.Provider,
                        $"MT5: COUNT_TICKS '{key}' — EA returned status='{status}' message='{msg}'");
                }
                return null;
            }

            long count64 = root.GetProperty("count").GetInt64();
            int count32 = count64 > int.MaxValue ? int.MaxValue : (int)count64;

            int chunks = root.TryGetProperty("chunks", out var ch) ? ch.GetInt32() : 1;

            var result = new Mt5CountResult(
                count32,
                root.GetProperty("first_msc").GetInt64(),
                root.GetProperty("last_msc").GetInt64(),
                root.GetProperty("perf_us").GetInt64());

            LogManager.Info(LogTag.Provider,
                $"MT5: COUNT_TICKS '{key}' [{fromMsc}..{toMsc}) — {result.Count:N0} ticks " +
                $"in {result.PerfUs / 1000.0:F1}ms across {chunks} chunk(s) " +
                $"(wall {sw.ElapsedMilliseconds}ms, timeout {adaptiveTimeoutMs}ms)");

            return result;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: COUNT_TICKS parse failed", ex);
            return null;
        }
        finally
        {
            _server.CountProgress -= handler;
        }
    }

    public async Task<List<Mt5Tick>> FetchTicksAsync(
        string symbol,
        long fromMsc,
        int targetTicks,
        int chunkSize,
        IProgress<Mt5FetchProgress>? progress,
        CancellationToken ct,
        Func<IReadOnlyList<Mt5Tick>, Task>? onBatch = null)
    {
        symbol = symbol?.Trim() ?? "";

        LogManager.Info(LogTag.Provider,
            $"MT5: FetchTicksAsync ENTRY symbol='{symbol}' fromMsc={fromMsc} target={targetTicks} batch={chunkSize}");

        if (symbol.Length == 0)
        {
            LogManager.Warn(LogTag.Provider, "MT5: FetchTicksAsync — symbol empty after trim");
            return new List<Mt5Tick>();
        }

        if (!_server.IsClientConnected)
        {
            LogManager.Warn(LogTag.Provider, "MT5: FetchTicksAsync — EA not connected");
            return new List<Mt5Tick>();
        }

        if (chunkSize <= 0)      chunkSize = 2000;
        if (chunkSize > 500_000) chunkSize = 500_000;

        progress?.Report(new Mt5FetchProgress(
            Fetched: 0,
            Total: targetTicks,
            Phase: "Prewarming symbol...",
            Percent: 0));
        await PrewarmAsync(symbol, fromMsc, ct);

        _lastEmittedTimeMsc = -1;
        _lastEmittedTrailing = 0;

        var result = new List<Mt5Tick>(Math.Min(targetTicks, 500_000));
        long cursor = fromMsc;
        int remaining = targetTicks;
        int iteration = 0;
        int totalChunks = targetTicks > 0
            ? (targetTicks + chunkSize - 1) / chunkSize
            : 0;
        int totalDeduped = 0;

        const int WindowSize = 5;
        var window = new Queue<(long ElapsedMs, int Ticks)>(WindowSize);
        var wall = Stopwatch.StartNew();

        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            iteration++;
            int batchSize = Math.Min(remaining, chunkSize);

            string extra = $"\"symbol\":\"{symbol}\",\"from_msc\":{cursor},\"count\":{batchSize}";

            var rttSw = Stopwatch.StartNew();
            var response = await _server.SendRequestAsync(
                "GET_TICKS", extra, _config.Settings.Mt5TimeoutMs, ct);
            rttSw.Stop();
            long rttMs = rttSw.ElapsedMilliseconds;

            if (response == null)
            {
                LogManager.Warn(LogTag.Provider,
                    $"MT5: fetch iter #{iteration} — null response after {rttMs}ms");
                break;
            }

            var parseSw = Stopwatch.StartNew();
            if (!Mt5TickParser.TryParseTicks(
                    response,
                    batchSize,
                    out var batchTicks,
                    out long eaCopyUs,
                    out long eaSerUs,
                    out long nextFrom,
                    out string? parseErr))
            {
                parseSw.Stop();
                LogManager.Warn(LogTag.Provider,
                    $"MT5: fetch iter #{iteration} — parse failed: {parseErr} " +
                    "(check EA version — expected compact array format, Backs2TickBridge v1.14+)");
                break;
            }
            parseSw.Stop();
            long parseMs = parseSw.ElapsedMilliseconds;

            int rawBatchCount = batchTicks.Count;
            int dedupedThisBatch = 0;

            if (_lastEmittedTimeMsc >= 0
                && _lastEmittedTrailing > 0
                && batchTicks.Count > 0)
            {
                int startIdx = 0;
                int skipped = 0;
                while (startIdx < batchTicks.Count
                    && batchTicks[startIdx].TimeMsc == _lastEmittedTimeMsc
                    && skipped < _lastEmittedTrailing)
                {
                    startIdx++;
                    skipped++;
                }
                if (startIdx > 0)
                {
                    batchTicks.RemoveRange(0, startIdx);
                    dedupedThisBatch = startIdx;
                    totalDeduped += startIdx;
                }
            }

            if (batchTicks.Count > 0)
            {
                long lastTime = batchTicks[^1].TimeMsc;
                int trailing = 0;
                for (int i = batchTicks.Count - 1; i >= 0; i--)
                {
                    if (batchTicks[i].TimeMsc == lastTime) trailing++;
                    else break;
                }
                _lastEmittedTimeMsc = lastTime;
                _lastEmittedTrailing = trailing;
            }

            result.AddRange(batchTicks);
            remaining -= batchTicks.Count;

            long batchMs = 0;
            if (onBatch != null && batchTicks.Count > 0)
            {
                var batchSw = Stopwatch.StartNew();
                await onBatch(batchTicks);
                batchSw.Stop();
                batchMs = batchSw.ElapsedMilliseconds;
            }

            double usPerTick = eaCopyUs / (double)Math.Max(1, rawBatchCount);
            LogManager.Info(LogTag.Provider,
                $"MT5: fetch iter #{iteration} — kept {batchTicks.Count} of {rawBatchCount} | " +
                $"rtt={rttMs}ms parse={parseMs}ms batch={batchMs}ms | " +
                $"ea_copy={eaCopyUs / 1000.0:F1}ms ({usPerTick:F1}us/t) " +
                $"ea_ser={eaSerUs / 1000.0:F1}ms " +
                (dedupedThisBatch > 0 ? $"dedupe={dedupedThisBatch} " : "") +
                $"(cumulative {result.Count}, total-deduped {totalDeduped})");

            long nowMs = wall.ElapsedMilliseconds;
            window.Enqueue((nowMs, result.Count));
            while (window.Count > WindowSize) window.Dequeue();

            double ticksPerSec = 0;
            double etaSec = 0;
            if (window.Count >= 2)
            {
                var oldest = window.Peek();
                long dtMs = nowMs - oldest.ElapsedMs;
                int dTicks = result.Count - oldest.Ticks;
                if (dtMs > 0 && dTicks > 0)
                {
                    ticksPerSec = dTicks * 1000.0 / dtMs;
                    int left = Math.Max(0, targetTicks - result.Count);
                    etaSec = ticksPerSec > 0 ? left / ticksPerSec : 0;
                }
            }

            double pct = targetTicks > 0
                ? Math.Min(100.0, 100.0 * result.Count / targetTicks)
                : 0;

            progress?.Report(new Mt5FetchProgress(
                Fetched: result.Count,
                Total: targetTicks,
                Phase: "Fetching",
                NewTicks: batchTicks,
                Percent: pct,
                TicksPerSecond: ticksPerSec,
                EtaSeconds: etaSec,
                CurrentChunk: iteration,
                TotalChunks: totalChunks));

            if (nextFrom <= 0 || batchTicks.Count == 0)
                break;

            cursor = nextFrom;

            if (rawBatchCount < batchSize)
                break;
        }

        LogManager.Info(LogTag.Provider,
            $"MT5: FetchTicksAsync EXIT — {result.Count} ticks across {iteration} iteration(s), {totalDeduped} boundary dupes dropped");

        progress?.Report(new Mt5FetchProgress(
            Fetched: result.Count,
            Total: result.Count,
            Phase: "Done",
            Percent: 100,
            CurrentChunk: iteration,
            TotalChunks: totalChunks));

        return result;
    }
}