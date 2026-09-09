using System.Threading.Tasks;
using backs2.Core;

namespace backs2.Core.Providers;

public class YahooProvider : IDataProvider 
{
    public string Name => "Yahoo";
    
    public async Task<string> FetchAsync(string symbol) 
    {
        return await Task.FromResult("Data Fetched");
    }
}