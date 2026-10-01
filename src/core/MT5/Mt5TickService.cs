using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.MT5;

public class Mt5TickService
{
    private readonly ConfigManager _config;
    private readonly Mt5TickServer _server;

    // Symbols already prewarmed this process. First CopyTicks per symbol
    // costs ~2× the warm rate (tick-DB page-in on the MT5 side). We pay it
    // once, up front, so the first real fetch iteration runs at warm speed.
    private readonly HashSet<string> _warmedSymbols = new(StringComparer.Ordinal);
    private readonly object _warmedLock = new();

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

    /// <summary>
    /// Fires a single small CopyTicks call to page in the symbol's tick DB
    /// before the real fetch. Idempotent per-symbol per-process. Never throws,
    /// never blocks for more than Mt5TimeoutMs, and is safe to call before
    /// every fetch — subsequent calls short-circuit on the HashSet.
    /// </summary>
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

        var result = new List<Mt5Tick>(Math.Min(targetTicks, 500_000));
        long cursor = fromMsc;
        int remaining = targetTicks;
        int iteration = 0;
        int totalChunks = targetTicks > 0
            ? (targetTicks + chunkSize - 1) / chunkSize
            : 0;

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

            try
            {
                long parseMs = 0;
                long batchMs = 0;
                long eaCopyUs = 0;
                long eaSerUs = 0;

                var parseSw = Stopwatch.StartNew();
                var batchTicks = new List<Mt5Tick>(batchSize);
                long nextFrom = 0;

                using (var doc = JsonDocument.Parse(response))
                {
                    var root = doc.RootElement;

                    var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (status != "OK")
                    {
                        var msg = root.TryGetProperty("message", out var m) ? m.GetString() : status;
                        LogManager.Warn(LogTag.Provider,
                            $"MT5: fetch iter #{iteration} — EA returned status='{status}' message='{msg}'");
                        break;
                    }

                    if (root.TryGetProperty("perf_copy_us", out var pcu)) eaCopyUs = pcu.GetInt64();
                    if (root.TryGetProperty("perf_ser_us",  out var psu)) eaSerUs  = psu.GetInt64();

                    foreach (var t in root.GetProperty("ticks").EnumerateArray())
                    {
                        batchTicks.Add(new Mt5Tick(
                            t.GetProperty("time_msc").GetInt64(),
                            t.GetProperty("bid").GetDouble(),
                            t.GetProperty("ask").GetDouble(),
                            t.GetProperty("last").GetDouble(),
                            t.GetProperty("volume").GetUInt64(),
                            t.GetProperty("volume_real").GetDouble(),
                            t.GetProperty("flags").GetUInt32()));
                    }

                    if (root.TryGetProperty("next_from_msc", out var nf)) nextFrom = nf.GetInt64();
                }
                parseSw.Stop();
                parseMs = parseSw.ElapsedMilliseconds;

                result.AddRange(batchTicks);
                remaining -= batchTicks.Count;

                if (onBatch != null && batchTicks.Count > 0)
                {
                    var batchSw = Stopwatch.StartNew();
                    await onBatch(batchTicks);
                    batchSw.Stop();
                    batchMs = batchSw.ElapsedMilliseconds;
                }

                double usPerTick = eaCopyUs / (double)Math.Max(1, batchTicks.Count);
                LogManager.Info(LogTag.Provider,
                    $"MT5: fetch iter #{iteration} — {batchTicks.Count} ticks | " +
                    $"rtt={rttMs}ms parse={parseMs}ms batch={batchMs}ms | " +
                    $"ea_copy={eaCopyUs / 1000.0:F1}ms ({usPerTick:F1}us/t) " +
                    $"ea_ser={eaSerUs / 1000.0:F1}ms " +
                    $"(cumulative {result.Count})");

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

                if (batchTicks.Count < batchSize)
                    break;
            }
            catch (JsonException jex)
            {
                LogManager.Error(LogTag.Provider,
                    $"MT5: fetch iter #{iteration} — JSON parse failed", jex);
                break;
            }
        }

        LogManager.Info(LogTag.Provider,
            $"MT5: FetchTicksAsync EXIT — {result.Count} ticks across {iteration} iteration(s)");

        progress?.Report(new Mt5FetchProgress(
            Fetched: result.Count,
            Total: result.Count,
            Phase: "Done",
            Percent: 100,
            CurrentChunk: iteration,
            TotalChunks: totalChunks));

        return result;
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