// src\core\Providers\TiingoProvider.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public class TiingoProvider : IDataProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigManager _config;
    private readonly LogManager _log;

    public string Name => "Tiingo";

    public TiingoProvider(IHttpClientFactory httpClientFactory, ConfigManager config, LogManager log)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _log = log;
    }

    public Task<List<Kline>> FetchAsync(string symbol) => FetchAsync(symbol, "1D");

    public async Task<List<Kline>> FetchAsync(string symbol, string timeframe)
    {
        if (string.IsNullOrWhiteSpace(_config.TiingoApiKey))
        {
            _log.Error($"[{Name}] API key missing. Add TIINGO_API_KEY to .env");
            return new List<Kline>();
        }

        var mappedTf = TimeframeMap.ToTiingo(timeframe);
        if (mappedTf == null)
        {
            _log.Error($"[{Name}] Timeframe {timeframe} not supported natively.");
            return new List<Kline>();
        }

        bool isIntraday = TimeframeMap.IsIntraday(timeframe);
        var lookback = TimeframeMap.LookbackDays(timeframe);
        var startDate = DateTime.UtcNow.AddDays(-lookback).ToString("yyyy-MM-dd");

        string url;
        if (isIntraday)
        {
            url = $"https://api.tiingo.com/iex/{symbol}/prices?startDate={startDate}&resampleFreq={mappedTf}&token={_config.TiingoApiKey}";
        }
        else
        {
            url = $"https://api.tiingo.com/tiingo/daily/{symbol}/prices?startDate={startDate}&token={_config.TiingoApiKey}";
        }

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail))
                {
                    _log.Error($"[{Name}] API returned error: {detail.GetString()}");
                }
                else
                {
                    _log.Error($"[{Name}] Unexpected JSON structure. Expected an array.");
                }
                return new List<Kline>();
            }

            var result = new List<Kline>();
            foreach (var item in root.EnumerateArray())
            {
                var tStr = item.GetProperty("date").GetString();
                if (string.IsNullOrEmpty(tStr) || !DateTime.TryParse(tStr, out var dt)) continue;

                var o = item.GetProperty("open").GetDouble();
                var h = item.GetProperty("high").GetDouble();
                var l = item.GetProperty("low").GetDouble();
                var c = item.GetProperty("close").GetDouble();

                long v = 0;
                if (item.TryGetProperty("volume", out var vProp) && vProp.ValueKind != JsonValueKind.Null)
                {
                    v = (long)vProp.GetDouble();
                }

                result.Add(new Kline(dt, o, h, l, c, v));
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.Error($"[{Name}] Error fetching data: {ex.Message}");
            return new List<Kline>();
        }
    }
}