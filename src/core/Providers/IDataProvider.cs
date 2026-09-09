using System.Threading.Tasks;

namespace backs2.Core.Providers;

public interface IDataProvider 
{
    string Name { get; }
    Task<string> FetchAsync(string symbol);
}