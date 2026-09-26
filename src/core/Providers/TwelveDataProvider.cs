// src\core\Providers\TwelveDataProvider.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public class TwelveDataProvider : IDataProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigManager _config;

    public string Name => "Twelve Data";

    public TwelveDataProvider(IHttpClientFactory httpClientFactory, ConfigManager config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public Task<List<Kline>> FetchAsync(string symbol) => FetchAsync(symbol, "1D");

    public async Task<List<Kline>> FetchAsync(string symbol, string timeframe)
    {
        if (string.IsNullOrWhiteSpace(_config.TwelveDataApiKey))
        {
            LogManager.Error(LogTag.Provider, $"{Name}: API key missing. Add TWELVEDATA_API_KEY to .env");
            return new List<Kline>();
        }

        var mappedTf = TimeframeMap.ToTwelveData(timeframe);
        if (mappedTf == null)
        {
            LogManager.Error(LogTag.Provider, $"{Name}: timeframe {timeframe} not supported natively");
            return new List<Kline>();
        }

        string cleanSymbol = symbol;
        if (symbol.Length == 6 && !symbol.Contains('/'))
        {
            cleanSymbol = $"{symbol.Substring(0, 3)}/{symbol.Substring(3, 3)}";
        }

        string url = $"https://api.twelvedata.com/time_series?symbol={cleanSymbol}&interval={mappedTf}&outputsize=5000&apikey={_config.TwelveDataApiKey}";

        LogManager.Debug(LogTag.Provider, $"{Name}: GET {cleanSymbol}@{timeframe} interval={mappedTf}");

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "error")
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "Unknown API error";
                LogManager.Error(LogTag.Provider, $"{Name}: API returned error: {msg}");
                return new List<Kline>();
            }

            if (!root.TryGetProperty("values", out var valuesArray))
            {
                LogManager.Warn(LogTag.Provider, $"{Name}: empty values array or invalid JSON format");
                return new List<Kline>();
            }

            var result = new List<Kline>();
            foreach (var item in valuesArray.EnumerateArray())
            {
                var tStr = item.GetProperty("datetime").GetString();
                if (string.IsNullOrEmpty(tStr) || !DateTime.TryParse(tStr, out var dt)) continue;

                var oStr = item.GetProperty("open").GetString();
                var hStr = item.GetProperty("high").GetString();
                var lStr = item.GetProperty("low").GetString();
                var cStr = item.GetProperty("close").GetString();

                if (oStr == null || !double.TryParse(oStr, CultureInfo.InvariantCulture, out var o)) continue;
                if (hStr == null || !double.TryParse(hStr, CultureInfo.InvariantCulture, out var h)) continue;
                if (lStr == null || !double.TryParse(lStr, CultureInfo.InvariantCulture, out var l)) continue;
                if (cStr == null || !double.TryParse(cStr, CultureInfo.InvariantCulture, out var c)) continue;

                long v = 0;
                if (item.TryGetProperty("volume", out var vProp) && vProp.ValueKind != JsonValueKind.Null)
                {
                    var vStr = vProp.GetString();
                    if (vStr != null)
                    {
                        if (long.TryParse(vStr, out var parsedLong))
                            v = parsedLong;
                        else if (double.TryParse(vStr, CultureInfo.InvariantCulture, out var parsedDouble))
                            v = (long)parsedDouble;
                    }
                }

                result.Add(new Kline(dt, o, h, l, c, v));
            }

            result.Reverse();
            LogManager.Info(LogTag.Provider, $"{Name}: {cleanSymbol}@{timeframe} → {result.Count} bars");
            return result;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, $"{Name}: {cleanSymbol}@{timeframe} failed", ex);
            return new List<Kline>();
        }
    }
}