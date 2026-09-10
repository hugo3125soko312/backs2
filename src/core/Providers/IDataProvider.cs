using System.Collections.Generic;
using System.Threading.Tasks;
using backs2.Core.Models;

namespace backs2.Core.Providers;

public interface IDataProvider
{
    string Name { get; }
    Task<List<Kline>> FetchAsync(string symbol);
}