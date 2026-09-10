using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace backs2.Core;

public class CacheManager
{
    private readonly string _cacheDir = "data/cache";

    public CacheManager() { if (!Directory.Exists(_cacheDir)) Directory.CreateDirectory(_cacheDir); }

    public async Task SaveCacheAsync<T>(string symbol, T data) 
    {
        var path = Path.Combine(_cacheDir, $"{symbol}.json");
        using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data);
    }

    public async Task<T?> LoadCacheAsync<T>(string symbol)
    {
        var path = Path.Combine(_cacheDir, $"{symbol}.json");
        if (!File.Exists(path)) return default;

        using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream);
    }
}