using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.MT5;

public class Mt5TickService
{
    // v1.16: default chunk size for chunked COUNT_TICKS. 6 hours bounds
    // per-chunk memory at ~300 MB worst-case on a hot forex symbol, and
    // gives the C# side a PROGRESS message roughly every 0.5–2 s.
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

    /// <summary>
    /// Ground-truth tick count for [fromMsc, toMsc) via the EA's COUNT_TICKS
    /// action, which calls CopyTicksRange directly.
    ///
    /// v1.16: chunked. The EA splits the range into 6-hour pieces, processes
    /// one piece per OnTimer tick, and streams PROGRESS messages between
    /// them. This lets the caller show liveness during multi-minute counts
    /// and prevents the terminal from allocating one huge MqlTick array.
    ///
    /// Error 4401 (ERR_HISTORY_NOT_FOUND) is logged at Info, not Warn — it
    /// means the range is beyond the broker's available tick history, which
    /// is a legitimate user error, not a bridge fault.
    /// </summary>
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

        // Wire up progress forwarding for the duration of this call. The
        // EA serializes chunked requests, so any PROGRESS arriving between
        // now and the response belongs to us.
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

            // v1.16: count is Int64 on the wire because a wide range can
            // exceed Int32. Cast to Int32 for Mt5CountResult — ranges above
            // 2.1B ticks are outside the current UI's display budget anyway.
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

            string extra =
                $"\"symbol\":\"{symbol}\",\"from_msc\":{cursor},\"count\":{batchSize}";

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
            if (!TryParseTicks(
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

    private static bool TryParseTicks(
        ReadOnlySpan<byte> json,
        int expectedCount,
        out List<Mt5Tick> ticks,
        out long perfCopyUs,
        out long perfSerUs,
        out long nextFromMsc,
        out string? error)
    {
        ticks = new List<Mt5Tick>(expectedCount);
        perfCopyUs = 0;
        perfSerUs = 0;
        nextFromMsc = 0;
        error = null;

        string? status = null;
        string? message = null;

        var reader = new Utf8JsonReader(json);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            error = "expected JSON object at top level";
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                error = $"unexpected token {reader.TokenType}";
                return false;
            }

            if (reader.ValueTextEquals("status"u8))
            {
                if (!reader.Read()) { error = "truncated after status"; return false; }
                if (reader.TokenType == JsonTokenType.String) status = reader.GetString();
            }
            else if (reader.ValueTextEquals("perf_copy_us"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                { error = "perf_copy_us not a number"; return false; }
                perfCopyUs = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("perf_ser_us"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                { error = "perf_ser_us not a number"; return false; }
                perfSerUs = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("next_from_msc"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                { error = "next_from_msc not a number"; return false; }
                nextFromMsc = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("message"u8))
            {
                if (!reader.Read()) { error = "truncated after message"; return false; }
                if (reader.TokenType == JsonTokenType.String) message = reader.GetString();
            }
            else if (reader.ValueTextEquals("ticks"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                { error = "ticks not an array"; return false; }

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) break;
                    if (reader.TokenType != JsonTokenType.Number)
                    { error = $"tick field not number (got {reader.TokenType})"; return false; }

                    long timeMsc = reader.GetInt64();

                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                    { error = "truncated at bid"; return false; }
                    double bid = reader.GetDouble();

                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                    { error = "truncated at ask"; return false; }
                    double ask = reader.GetDouble();

                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                    { error = "truncated at last"; return false; }
                    double last = reader.GetDouble();

                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                    { error = "truncated at volume"; return false; }
                    ulong volume = reader.GetUInt64();

                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                    { error = "truncated at volume_real"; return false; }
                    double volumeReal = reader.GetDouble();

                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                    { error = "truncated at flags"; return false; }
                    uint flags = reader.GetUInt32();

                    ticks.Add(new Mt5Tick(timeMsc, bid, ask, last, volume, volumeReal, flags));
                }
            }
            else
            {
                reader.Skip();
            }
        }

        if (status != "OK")
        {
            error = message ?? $"EA status={status ?? "(missing)"}";
            return false;
        }

        return true;
    }

    public async Task<Mt5TerminalInfo?> GetTerminalInfoAsync(CancellationToken ct)
    {
        var response = await _server.SendRequestAsync(
            "GET_TERMINAL_INFO", "", _config.Settings.Mt5TimeoutMs, ct);
        if (response == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() != "OK") return null;

            return new Mt5TerminalInfo(
                root.GetProperty("company").GetString() ?? "",
                root.GetProperty("account").GetInt64(),
                root.GetProperty("server").GetString() ?? "",
                root.GetProperty("currency").GetString() ?? "",
                root.GetProperty("leverage").GetInt64(),
                DateTimeOffset.FromUnixTimeSeconds(
                    root.GetProperty("server_time").GetInt64()).UtcDateTime);
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: terminal info parse failed", ex);
            return null;
        }
    }
}