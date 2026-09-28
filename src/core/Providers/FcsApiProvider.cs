// src/core/Providers/FcsApiProvider.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public class FcsApiProvider : IDataProvider
{
    private const string BaseUrl = "https://api-v4.fcsapi.com";
    private const int MaxLength = 500;

    // Commodity base prefixes. These route with type=commodity and use the
    // concatenated symbol form (XAUUSD), not the slash form (EUR/USD).
    private static readonly string[] CommodityPrefixes =
    {
        "XAU", "XAG", "XPT", "XPD", "WTI", "BRENT", "NGAS", "COPPER", "OIL"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigManager _config;

    public string Name => "FCS API";

    public FcsApiProvider(IHttpClientFactory httpClientFactory, ConfigManager config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public Task<List<Kline>> FetchAsync(string symbol) => FetchAsync(symbol, "1D");

    public async Task<List<Kline>> FetchAsync(string symbol, string timeframe)
    {
        if (string.IsNullOrWhiteSpace(_config.FcsApiKey))
        {
            LogManager.Error(LogTag.Provider, $"{Name}: API key missing. Add FCS_PUBLIC_API_KEY to .env");
            return new List<Kline>();
        }

        var period = TimeframeMap.ToFcs(timeframe);
        if (period == null)
        {
            LogManager.Warn(LogTag.Provider, $"{Name}: timeframe {timeframe} not supported");
            return new List<Kline>();
        }

        bool isCommodity = IsCommodity(symbol);
        string resolved = isCommodity ? ResolveCommoditySymbol(symbol) : ResolveForexSymbol(symbol);

        var url = $"{BaseUrl}/forex/history?symbol={resolved}&period={period}" +
                  $"&length={MaxLength}&access_key={_config.FcsApiKey}";

        if (isCommodity)
            url += "&type=commodity";

        LogManager.Debug(LogTag.Provider,
            $"{Name}: GET {resolved}@{timeframe} period={period} type={(isCommodity ? "commodity" : "forex")}");

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var status) && !status.GetBoolean())
            {
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString()
                        : root.TryGetProperty("message", out var m2) ? m2.GetString()
                        : "unknown";
                LogManager.Warn(LogTag.Provider, $"{Name}: API returned status=false: {msg} ({resolved}@{timeframe})");
                return new List<Kline>();
            }

            if (!root.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object)
            {
                LogManager.Warn(LogTag.Provider, $"{Name}: no response object");
                return new List<Kline>();
            }

            var result = new List<Kline>();

            foreach (var prop in resp.EnumerateObject())
            {
                if (!long.TryParse(prop.Name, out var unixSec)) continue;
                var candle = prop.Value;

                if (!candle.TryGetProperty("o", out var oProp)) continue;
                if (!candle.TryGetProperty("h", out var hProp)) continue;
                if (!candle.TryGetProperty("l", out var lProp)) continue;
                if (!candle.TryGetProperty("c", out var cProp)) continue;

                var o = oProp.GetDouble();
                var h = hProp.GetDouble();
                var l = lProp.GetDouble();
                var c = cProp.GetDouble();

                long v = 0;
                if (candle.TryGetProperty("v", out var vProp) && vProp.ValueKind == JsonValueKind.Number)
                    v = (long)vProp.GetDouble();

                result.Add(new Kline(
                    DateTimeOffset.FromUnixTimeSeconds(unixSec).UtcDateTime,
                    o, h, l, c, v));
            }

            result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            if (result.Count == 0)
            {
                LogManager.Warn(LogTag.Provider, $"{Name}: {resolved}@{timeframe} parsed zero bars");
                return new List<Kline>();
            }

            LogManager.Info(LogTag.Provider, $"{Name}: {resolved}@{timeframe} → {result.Count} bars");
            return result;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, $"{Name}: {resolved}@{timeframe} failed", ex);
            return new List<Kline>();
        }
    }

    private static bool IsCommodity(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var upper = symbol.ToUpperInvariant();
        foreach (var p in CommodityPrefixes)
            if (upper.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string ResolveCommoditySymbol(string symbol)
    {
        // Commodities use concatenated form: XAU/USD -> XAUUSD, XAUUSD -> XAUUSD
        return symbol.ToUpperInvariant().Replace("/", "").Replace("-", "");
    }

    private static string ResolveForexSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return symbol;
        if (symbol.Contains('/')) return symbol;
        if (symbol.Length == 6 && !symbol.Contains('-'))
            return $"{symbol.Substring(0, 3)}/{symbol.Substring(3, 3)}";
        return symbol;
    }
}