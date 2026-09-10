using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core;
using backs2.Core.Models;

namespace backs2.ViewModels;

public partial class StratsViewModel : ObservableObject
{
    private readonly StrategyManager _strat;
    private readonly DataManager _data;

    [ObservableProperty]
    private double _sharpe;

    [ObservableProperty]
    private double _winRate;

    // Inject DataManager here
    public StratsViewModel(StrategyManager strat, DataManager data)
    {
        _strat = strat;
        _data = data;
    }

    public async Task RunAndDisplay(string stratId, string symbol)
    {
        // 1. Fetch the actual list of Klines from DataManager
        var data = await _data.GetSymbolDataAsync(symbol);
        
        // 2. Pass the list, not the string, to the strategy manager
        await _strat.RunBacktestAsync(stratId, data);
    }
}