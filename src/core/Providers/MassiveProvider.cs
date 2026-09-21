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
    private const int DefaultLookbackDays = 365;

    private static readonly HashSet<string> FatalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT_AUTHORIZED",
        "ERROR",
        "NOT_FOUND"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigManager _config;
    private readonly LogManager _logger;

    public string Name => "Massive";

    public MassiveProvider(IHttpClientFactory httpClientFactory, ConfigManager config, LogManager logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<List<Kline>> FetchAsync(string symbol)
    {
        var apiKey = _config.MassiveApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.Error("MassiveProvider: MASSIVE_PROVIDER_API_KEY missing in .env");
            return new List<Kline>();
        }

        var resolved = ResolveSymbol(symbol);
        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-DefaultLookbackDays);

        var url = $"{BaseUrl}/v2/aggs/ticker/{Uri.EscapeDataString(resolved)}/range/1/day/" +
                  $"{from:yyyy-MM-dd}/{to:yyyy-MM-dd}" +
                  $"?adjusted=true&sort=asc&limit=50000&apiKey={Uri.EscapeDataString(apiKey)}";

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.Error($"MassiveProvider: HTTP {(int)response.StatusCode} for {resolved} — {Truncate(body, 200)}");
                return new List<Kline>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            string? status = null;
            if (root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
                status = statusEl.GetString();

            // OK and DELAYED both carry usable results. Only fatal statuses abort.
            if (status != null && FatalStatuses.Contains(status))
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                _logger.Error($"MassiveProvider: status '{status}' for {resolved} — {msg}");
                return new List<Kline>();
            }

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                _logger.Info($"MassiveProvider: no results for {resolved} (status '{status ?? "none"}')");
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
                    o.GetDouble(),
                    h.GetDouble(),
                    l.GetDouble(),
                    c.GetDouble(),
                    vol));
            }

            var delayNote = string.Equals(status, "DELAYED", StringComparison.OrdinalIgnoreCase) ? " (delayed)" : "";
            _logger.Info($"MassiveProvider: {resolved} → {klines.Count} bars{delayNote}");
            return klines;
        }
        catch (Exception ex)
        {
            _logger.Error($"MassiveProvider: {symbol} ({resolved}) failed — {ex.Message}");
            return new List<Kline>();
        }
    }

    private static string ResolveSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return symbol;
        symbol = symbol.Trim().ToUpperInvariant();

        if (symbol.Length > 2 && symbol[1] == ':') return symbol;

        if (symbol.EndsWith("=X")) symbol = symbol[..^2];

        if (symbol.Length == 6 && symbol.All(char.IsLetter))
            return "C:" + symbol;

        return symbol;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}