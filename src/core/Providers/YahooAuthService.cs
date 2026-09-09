using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace backs2.Core.Providers;

public class YahooAuthService 
{
    private static readonly HttpClient _httpClient = new();
    
    public async Task<string> GetCrumbAsync() 
    {
        return await Task.FromResult("dynamic-crumb-value");
    }
}