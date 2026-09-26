using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public class MassiveProvider : IDataProvider
{
    private const string BaseUrl = "https://api.massive.com";

    private static readonly HashSet<string> FatalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT_AUTHORIZED",
        "ERROR",
        "NOT_FOUND"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigManager _config;

    public string Name => "Massive";

    public MassiveProvider(IHttpClientFactory httpClientFactory, ConfigManager config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public Task<List<Kline>> FetchAsync(string symbol) => FetchAsync(symbol, "1D");

    public async Task<List<Kline>> FetchAsync(string symbol, string timeframe)
    {
        var apiKey = _config.MassiveApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LogManager.Error(LogTag.Provider, "Massive: MASSIVE_PROVIDER_API_KEY missing in .env");
            return new List<Kline>();
        }

        var tf = TimeframeMap.ToMassive(timeframe);
        if (tf == null)
        {
            LogManager.Error(LogTag.Provider, $"Massive: unsupported timeframe '{timeframe}'");
            return new List<Kline>();
        }

        var resolved = ResolveSymbol(symbol);
        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-TimeframeMap.LookbackDays(timeframe));

        var url = $"{BaseUrl}/v2/aggs/ticker/{Uri.EscapeDataString(resolved)}/range/" +
                  $"{tf.Value.Multiplier}/{tf.Value.Timespan}/" +
                  $"{from:yyyy-MM-dd}/{to:yyyy-MM-dd}" +
                  $"?adjusted=true&sort=asc&limit=50000&apiKey={Uri.EscapeDataString(apiKey)}";

        LogManager.Debug(LogTag.Provider, $"Massive: GET {resolved}@{timeframe} from={from:yyyy-MM-dd} to={to:yyyy-MM-dd}");

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                LogManager.Error(LogTag.Provider,
                    $"Massive: HTTP {(int)response.StatusCode} for {resolved}@{timeframe} — {Truncate(body, 200)}");
                return new List<Kline>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            string? status = null;
            if (root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
                status = statusEl.GetString();

            if (status != null && FatalStatuses.Contains(status))
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                LogManager.Error(LogTag.Provider,
                    $"Massive: status '{status}' for {resolved}@{timeframe} — {msg}");
                return new List<Kline>();
            }

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                LogManager.Warn(LogTag.Provider,
                    $"Massive: no results for {resolved}@{timeframe} (status '{status ?? "none"}')");
                return new List<Kline>();
            }

            var klines = new List<Kline>(results.GetArrayLength());

            foreach (var bar in results.EnumerateArray())
            {
                if (!bar.TryGetProperty("t", out var t)) continue;
                if (!bar.TryGetProperty("o", out var o)) continue;
                if (!bar.TryGetProperty("h", out var h)) continue;
                if (!bar.TryGetProperty("l", out var l)) continue;
                if (!bar.TryGetProperty("c", out var c)) continue;

                var vol = bar.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number
                    ? (long)v.GetDouble()
                    : 0L;

                klines.Add(new Kline(
                    DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()).UtcDateTime,
                    o.GetDouble(), h.GetDouble(), l.GetDouble(), c.GetDouble(), vol));
            }

            var delayNote = string.Equals(status, "DELAYED", StringComparison.OrdinalIgnoreCase) ? " (delayed)" : "";
            LogManager.Info(LogTag.Provider, $"Massive: {resolved}@{timeframe} → {klines.Count} bars{delayNote}");
            return klines;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider,
                $"Massive: {symbol}@{timeframe} ({resolved}) failed", ex);
            return new List<Kline>();
        }
    }

    private static string ResolveSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return symbol;
        symbol = symbol.Trim().ToUpperInvariant();

        if (symbol.Length > 2 && symbol[1] == ':') return symbol;
        if (symbol.EndsWith("=X")) symbol = symbol[..^2];
        if (symbol.Length == 6 && symbol.All(char.IsLetter)) return "C:" + symbol;

        return symbol;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}