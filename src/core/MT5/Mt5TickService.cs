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

    public async Task<List<Mt5Tick>> FetchTicksAsync(
        string symbol,
        long fromMsc,
        int targetTicks,
        int chunkSize,
        IProgress<Mt5FetchProgress>? progress,
        CancellationToken ct)
    {
        symbol = symbol?.Trim() ?? "";

        LogManager.Info(LogTag.Provider,
            $"MT5: FetchTicksAsync ENTRY symbol='{symbol}' fromMsc={fromMsc} target={targetTicks} chunk={chunkSize}");

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

        if (chunkSize <= 0)   chunkSize = 200;
        if (chunkSize > 2000) chunkSize = 2000;   // EA hard cap

        var result = new List<Mt5Tick>(Math.Min(targetTicks, 200_000));
        long cursor = fromMsc;
        int remaining = targetTicks;
        int iteration = 0;

        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            iteration++;
            int batchSize = Math.Min(remaining, chunkSize);

            string extra =
                $"\"symbol\":\"{symbol}\",\"from_msc\":{cursor},\"count\":{batchSize}";

            var sw = Stopwatch.StartNew();
            var response = await _server.SendRequestAsync(
                "GET_TICKS", extra, _config.Settings.Mt5TimeoutMs, ct);
            sw.Stop();

            if (response == null)
            {
                LogManager.Warn(LogTag.Provider,
                    $"MT5: fetch iter #{iteration} — null response after {sw.ElapsedMilliseconds}ms");
                break;
            }

            try
            {
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
                if (status != "OK")
                {
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() : status;
                    LogManager.Warn(LogTag.Provider,
                        $"MT5: fetch iter #{iteration} — EA returned status='{status}' message='{msg}'");
                    break;
                }

                var batchTicks = new List<Mt5Tick>(batchSize);
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

                result.AddRange(batchTicks);
                remaining -= batchTicks.Count;

                LogManager.Info(LogTag.Provider,
                    $"MT5: fetch iter #{iteration} — {batchTicks.Count} ticks in {sw.ElapsedMilliseconds}ms (cumulative {result.Count})");

                progress?.Report(new Mt5FetchProgress(
                    result.Count,
                    targetTicks,
                    "Fetching",
                    batchTicks));

                long nextFrom = root.TryGetProperty("next_from_msc", out var nf) ? nf.GetInt64() : 0;
                if (nextFrom <= 0 || batchTicks.Count == 0)
                    break;

                cursor = nextFrom;

                if (batchTicks.Count < batchSize)
                    break;   // broker ran out of history
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
        progress?.Report(new Mt5FetchProgress(result.Count, result.Count, "Done"));
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