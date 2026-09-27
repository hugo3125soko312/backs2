// src/core/Providers/BinanceProvider.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public class BinanceProvider : IDataProvider
{
    private const string BaseUrl = "https://data-api.binance.vision";
    private const int MaxLimit = 1500;
    private static readonly long MaxSpanMs = (long)TimeSpan.FromDays(200).TotalMilliseconds;

    private readonly IHttpClientFactory _httpClientFactory;

    public string Name => "Binance";

    public BinanceProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<List<Kline>> FetchAsync(string symbol) => FetchAsync(symbol, "1D");

    public async Task<List<Kline>> FetchAsync(string symbol, string timeframe)
    {
        var interval = TimeframeMap.ToBinance(timeframe);
        if (interval == null)
        {
            LogManager.Warn(LogTag.Provider, $"{Name}: timeframe {timeframe} not supported");
            return new List<Kline>();
        }

        long endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long startMs = DateTimeOffset.UtcNow.AddDays(-TimeframeMap.LookbackDays(timeframe)).ToUnixTimeMilliseconds();

        LogManager.Debug(LogTag.Provider, $"{Name}: GET {symbol}@{timeframe} interval={interval}");

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var result = new List<Kline>();
            long cursor = startMs;

            while (cursor < endMs)
            {
                long chunkEnd = Math.Min(cursor + MaxSpanMs, endMs);
                var url = $"{BaseUrl}/api/v3/klines?symbol={symbol}&interval={interval}" +
                          $"&startTime={cursor}&endTime={chunkEnd}&limit={MaxLimit}";

                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    break;

                int count = 0;
                long lastOpenMs = cursor;

                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 6)
                        continue;

                    long openMs = item[0].GetInt64();
                    var open = double.Parse(item[1].GetString()!, CultureInfo.InvariantCulture);
                    var high = double.Parse(item[2].GetString()!, CultureInfo.InvariantCulture);
                    var low = double.Parse(item[3].GetString()!, CultureInfo.InvariantCulture);
                    var close = double.Parse(item[4].GetString()!, CultureInfo.InvariantCulture);
                    var volStr = item[5].GetString()!;
                    long vol = (long)double.Parse(volStr, CultureInfo.InvariantCulture);

                    result.Add(new Kline(
                        DateTimeOffset.FromUnixTimeMilliseconds(openMs).UtcDateTime,
                        open, high, low, close, vol));

                    lastOpenMs = openMs;
                    count++;
                }

                if (count == 0 || count < MaxLimit)
                    break;

                cursor = lastOpenMs + 1;
            }

            result.Reverse();

            if (result.Count == 0)
            {
                LogManager.Warn(LogTag.Provider, $"{Name}: {symbol}@{timeframe} parsed zero bars");
                return new List<Kline>();
            }

            LogManager.Info(LogTag.Provider, $"{Name}: {symbol}@{timeframe} → {result.Count} bars");
            return result;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, $"{Name}: {symbol}@{timeframe} failed", ex);
            return new List<Kline>();
        }
    }
}