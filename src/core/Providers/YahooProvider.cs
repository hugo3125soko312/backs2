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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LogManager _logger;

    public string Name => "Yahoo";

    public YahooProvider(IHttpClientFactory httpClientFactory, LogManager logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<List<Kline>> FetchAsync(string symbol) => FetchAsync(symbol, "1D");

    public async Task<List<Kline>> FetchAsync(string symbol, string timeframe)
    {
        var yahooInterval = TimeframeMap.ToYahoo(timeframe);
        if (yahooInterval == null)
        {
            _logger.Error($"YahooProvider: unsupported timeframe '{timeframe}'");
            return new List<Kline>();
        }

        var resolved = ResolveSymbol(symbol);
        var range = RangeForTimeframe(timeframe);
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{resolved}" +
                  $"?interval={yahooInterval}&range={range}";

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var json = await client.GetStringAsync(url);

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

            _logger.Info($"YahooProvider: {resolved}@{timeframe} → {klines.Count} bars");
            return klines;
        }
        catch (Exception ex)
        {
            _logger.Error($"YahooProvider: {symbol}@{timeframe} ({resolved}) failed — {ex.Message}");
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
        if (symbol.Length == 6 && symbol.All(char.IsLetter)) return symbol + "=X";
        return symbol;
    }
}