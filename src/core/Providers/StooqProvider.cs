// src/core/Providers/StooqProvider.cs
//DEAD for now, api key from 2026 has been discarded, automated access against tos, skipped for now
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public class StooqProvider : IDataProvider
{
    private const string BaseUrl = "https://stooq.com/q/d/l/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigManager _config;

    public string Name => "Stooq";

    public StooqProvider(IHttpClientFactory httpClientFactory, ConfigManager config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public Task<List<Kline>> FetchAsync(string symbol) => FetchAsync(symbol, "1D");

    public async Task<List<Kline>> FetchAsync(string symbol, string timeframe)
    {
        if (string.IsNullOrWhiteSpace(_config.StooqApiKey))
        {
            LogManager.Error(LogTag.Provider, $"{Name}: API key missing. Add STOOQ_API_KEY to .env (CAPTCHA-obtained)");
            return new List<Kline>();
        }

        var interval = TimeframeMap.ToStooq(timeframe);
        if (interval == null)
        {
            LogManager.Warn(LogTag.Provider, $"{Name}: timeframe {timeframe} not supported (d/w/m only)");
            return new List<Kline>();
        }

        string resolved = ResolveSymbol(symbol);
        int days = TimeframeMap.LookbackDays(timeframe);
        var d2 = DateTime.UtcNow;
        var d1 = d2.AddDays(-days);

        var url = $"{BaseUrl}?s={resolved}&i={interval}" +
                  $"&d1={d1:yyyyMMdd}&d2={d2:yyyyMMdd}&apikey={_config.StooqApiKey}";

        LogManager.Debug(LogTag.Provider, $"{Name}: GET {resolved}@{timeframe} interval={interval}");

        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var csv = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(csv) || csv.StartsWith("No data", StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Warn(LogTag.Provider, $"{Name}: {resolved}@{timeframe} no data returned");
                return new List<Kline>();
            }

            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                LogManager.Warn(LogTag.Provider, $"{Name}: {resolved}@{timeframe} CSV too short");
                return new List<Kline>();
            }

            var result = new List<Kline>(lines.Length - 1);

            for (int i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Trim().Split(',');
                if (parts.Length < 6) continue;

                if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                    continue;

                if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o)) continue;
                if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h)) continue;
                if (!double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l)) continue;
                if (!double.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var c)) continue;

                long v = 0;
                double.TryParse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var vd);
                v = (long)vd;

                result.Add(new Kline(DateTime.SpecifyKind(dt, DateTimeKind.Utc), o, h, l, c, v));
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

    private static string ResolveSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return symbol;
        var lower = symbol.ToLowerInvariant();
        if (lower.Contains('/')) return lower.Replace("/", "");
        if (lower.Contains('.')) return lower;
        if (lower.Length == 6 && char.IsLetter(lower[0]))
            return lower;
        return lower + ".us";
    }
}