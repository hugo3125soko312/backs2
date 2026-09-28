using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public class YahooProvider : IDataProvider
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";

    private readonly IHttpClientFactory _httpClientFactory;

    public string Name => "Yahoo";

    public YahooProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<List<Kline>> FetchAsync(string symbol) => FetchAsync(symbol, "1D");

    public async Task<List<Kline>> FetchAsync(string symbol, string timeframe)
    {
        var yahooInterval = TimeframeMap.ToYahoo(timeframe);
        if (yahooInterval == null)
        {
            LogManager.Warn(LogTag.Provider, $"Yahoo: timeframe {timeframe} not supported natively, skipping provider");
            return new List<Kline>();
        }

        var resolved = ResolveSymbol(symbol);
        var range = RangeForTimeframe(timeframe);
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{resolved}" +
                  $"?interval={yahooInterval}&range={range}";

        LogManager.Debug(LogTag.Provider, $"Yahoo: GET {resolved}@{timeframe} range={range}");

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("chart", out var chart)) return new List<Kline>();
            if (!chart.TryGetProperty("result", out var result)) return new List<Kline>();
            if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
                return new List<Kline>();

            var first = result[0];
            if (!first.TryGetProperty("timestamp", out var timestamps)) return new List<Kline>();
            if (!first.TryGetProperty("indicators", out var indicators)) return new List<Kline>();
            if (!indicators.TryGetProperty("quote", out var quotes)) return new List<Kline>();
            if (quotes.ValueKind != JsonValueKind.Array || quotes.GetArrayLength() == 0)
                return new List<Kline>();

            var q = quotes[0];
            var opens = q.GetProperty("open");
            var highs = q.GetProperty("high");
            var lows = q.GetProperty("low");
            var closes = q.GetProperty("close");
            var volumes = q.GetProperty("volume");

            var klines = new List<Kline>(timestamps.GetArrayLength());

            for (int i = 0; i < timestamps.GetArrayLength(); i++)
            {
                if (opens[i].ValueKind == JsonValueKind.Null) continue;
                if (highs[i].ValueKind == JsonValueKind.Null) continue;
                if (lows[i].ValueKind == JsonValueKind.Null) continue;
                if (closes[i].ValueKind == JsonValueKind.Null) continue;

                var ts = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime;
                var vol = volumes[i].ValueKind == JsonValueKind.Null ? 0L : volumes[i].GetInt64();

                klines.Add(new Kline(
                    ts,
                    opens[i].GetDouble(),
                    highs[i].GetDouble(),
                    lows[i].GetDouble(),
                    closes[i].GetDouble(),
                    vol));
            }

            LogManager.Info(LogTag.Provider, $"Yahoo: {resolved}@{timeframe} → {klines.Count} bars");
            return klines;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, $"Yahoo: {symbol}@{timeframe} ({resolved}) failed", ex);
            return new List<Kline>();
        }
    }

    private static string RangeForTimeframe(string tf) => tf switch
    {
        "1m"  => "5d",
        "2m"  => "1mo",
        "5m"  => "1mo",
        "15m" => "1mo",
        "30m" => "1mo",
        "1h"  => "6mo",
        "1D"  => "1y",
        "1W"  => "5y",
        _     => "1y"
    };

    private static string ResolveSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return symbol;
        if (symbol.Contains('=') || symbol.Contains('-')) return symbol;

        // Yahoo dropped spot precious-metal tickers (XAUUSD=X, XAGUSD=X, XPTUSD=X)
        // during the QFAgg migration. Map to the front-month futures contract instead.
        // Note: these are futures prices, not spot. 0.18 correlation with spot.
        switch (symbol.ToUpperInvariant())
        {
            case "XAUUSD": return "GC=F";
            case "XAGUSD": return "SI=F";
            case "XPTUSD": return "PL=F";
            case "XPDUSD": return "PA=F";
            case "HGUSD":  return "HG=F";
        }

        if (symbol.Length == 6 && symbol.All(char.IsLetter)) return symbol + "=X";
        return symbol;
    }
}