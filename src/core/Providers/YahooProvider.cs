using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public class YahooProvider : IDataProvider
{
    private static readonly HttpClient _httpClient = new HttpClient();

    // Required by IDataProvider interface
    public string Name => "Yahoo";

    public async Task<List<Kline>> FetchAsync(string symbol)
    {
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?interval=1d&range=1mo";
        
        try
        {
            var response = await _httpClient.GetStringAsync(url);
            // Parsing logic will go here later
            return new List<Kline>(); 
        }
        catch (Exception)
        {
            // LogManager should be injected here later if needed
            return new List<Kline>();
        }
    }
}