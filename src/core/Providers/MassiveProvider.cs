// File: src/core/Providers/MassiveProvider.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using backs2.Core;
using backs2.Core.Models;

namespace backs2.Core.Providers
{
    public class MassiveProvider : IDataProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ConfigManager _config;
        private readonly LogManager _logger;

        public string Name => "Massive.com";

        // We inject IHttpClientFactory to retrieve the "ResilientClient" 
        // configured in App.axaml.cs
        public MassiveProvider(IHttpClientFactory httpClientFactory, ConfigManager config, LogManager logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        public async Task<List<Kline>> FetchAsync(string symbol)
        {
            string apiKey = _config.Settings?.YahooApiKey ?? string.Empty;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.Error("MassiveProvider: API key missing in settings.");
                return new List<Kline>();
            }

            try
            {
                // Retrieve the ResilientClient that handles retries automatically
                var client = _httpClientFactory.CreateClient("ResilientClient");
                
                var requestUrl = $"https://api.massive.com/v1/market-data?symbol={symbol}&key={apiKey}";
                var response = await client.GetAsync(requestUrl);
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Error($"MassiveProvider: API Error {response.StatusCode} for {symbol}");
                    return new List<Kline>();
                }

                using var content = await response.Content.ReadAsStreamAsync();
                var result = await JsonSerializer.DeserializeAsync<List<Kline>>(content);
                
                _logger.Info($"MassiveProvider: Fetched data for {symbol}");
                return result ?? new List<Kline>();
            }
            catch (Exception ex)
            {
                // This catch block will trigger only if the resilience policies 
                // (max retries) are exhausted.
                _logger.Error($"MassiveProvider: Exception after retries - {ex.Message}");
                return new List<Kline>();
            }
        }
    }
}