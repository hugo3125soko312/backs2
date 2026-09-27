// src/core/Providers/FinnhubProvider.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public class FinnhubProvider : IDataProvider
{
    private const string BaseUrl = "https://finnhub.io/api/v1";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigManager _config;

    public string Name => "Finnhub";

    public FinnhubProvider(IHttpClientFactory httpClientFactory, ConfigManager config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public Task<List<Kline>> FetchAsync(string symbol) => FetchAsync(symbol, "1D");

    public async Task<List<Kline>> FetchAsync(string symbol, string timeframe)
    {
        if (string.IsNullOrWhiteSpace(_config.FinnhubApiKey))
        {
            LogManager.Error(LogTag.Provider, $"{Name}: API key missing. Add FINNHUB_API_KEY to .env");
            return new List<Kline>();
        }

        var resolution = TimeframeMap.ToFinnhub(timeframe);
        if (resolution == null)
        {
            LogManager.Warn(LogTag.Provider, $"{Name}: timeframe {timeframe} not supported");
            return new List<Kline>();
        }

        bool isForex = IsForexSymbol(symbol);
        string endpoint = isForex ? "forex/candle" : "stock/candle";
        string resolved = isForex ? ResolveForexSymbol(symbol) : symbol;

        long to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long from = DateTimeOffset.UtcNow.AddDays(-TimeframeMap.LookbackDays(timeframe)).ToUnixTimeSeconds();

        var url = $"{BaseUrl}/{endpoint}?symbol={resolved}&resolution={resolution}" +
                  $"&from={from}&to={to}&token={_config.FinnhubApiKey}";

        LogManager.Debug(LogTag.Provider, $"{Name}: GET {resolved}@{timeframe} resolution={resolution} endpoint={endpoint}");

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("s", out var statusProp))
            {
                LogManager.Warn(LogTag.Provider, $"{Name}: response missing status field");
                return new List<Kline>();
            }

            var status = statusProp.GetString();
            if (status != "ok")
            {
                LogManager.Warn(LogTag.Provider, $"{Name}: {resolved}@{timeframe} status={status} (paid-tier endpoint may be required)");
                return new List<Kline>();
            }

            if (!root.TryGetProperty("t", out var tArr) || tArr.GetArrayLength() == 0)
            {
                LogManager.Warn(LogTag.Provider, $"{Name}: empty candle arrays");
                return new List<Kline>();
            }

            var oArr = root.GetProperty("o");
            var hArr = root.GetProperty("h");
            var lArr = root.GetProperty("l");
            var cArr = root.GetProperty("c");
            var vArr = root.GetProperty("v");

            int n = tArr.GetArrayLength();
            var result = new List<Kline>(n);

            for (int i = 0; i < n; i++)
            {
                long t = tArr[i].GetInt64();
                var o = oArr[i].GetDouble();
                var h = hArr[i].GetDouble();
                var l = lArr[i].GetDouble();
                var c = cArr[i].GetDouble();
                long v = vArr[i].ValueKind == JsonValueKind.Null ? 0L : (long)vArr[i].GetDouble();

                result.Add(new Kline(
                    DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime,
                    o, h, l, c, v));
            }

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

    private static bool IsForexSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        if (symbol.StartsWith("OANDA:", StringComparison.OrdinalIgnoreCase)) return true;
        if (symbol.Contains('_')) return true;
        if (symbol.Length == 6 && char.IsLetter(symbol[0]) && char.IsLetter(symbol[5]))
        {
            var upper = symbol.ToUpperInvariant();
            return upper.EndsWith("USD") || upper.StartsWith("USD") ||
                   upper.EndsWith("EUR") || upper.EndsWith("GBP") ||
                   upper.EndsWith("JPY") || upper.EndsWith("CHF") ||
                   upper.EndsWith("AUD") || upper.EndsWith("NZD") ||
                   upper.EndsWith("CAD");
        }
        return false;
    }

    private static string ResolveForexSymbol(string symbol)
    {
        if (symbol.StartsWith("OANDA:", StringComparison.OrdinalIgnoreCase)) return symbol;
        if (symbol.Contains('_')) return $"OANDA:{symbol}";
        if (symbol.Length >= 6)
        {
            var baseCur = symbol.Substring(0, 3).ToUpperInvariant();
            var quoteCur = symbol.Substring(3, 3).ToUpperInvariant();
            return $"OANDA:{baseCur}_{quoteCur}";
        }
        return symbol;
    }
}