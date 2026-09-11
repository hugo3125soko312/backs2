using System;
using System.Collections.Generic;
using System.Net.Http;
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

    public async Task<List<Kline>> FetchAsync(string symbol)
    {
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?interval=1d&range=1mo";
        
        try
        {
            var client = _httpClientFactory.CreateClient("ResilientClient");
            var response = await client.GetStringAsync(url);
            
            // Parsing logic will go here later
            return new List<Kline>(); 
        }
        catch (Exception ex)
        {
            _logger.Error($"YahooProvider: Failed to fetch {symbol}: {ex.Message}");
            return new List<Kline>();
        }
    }
}