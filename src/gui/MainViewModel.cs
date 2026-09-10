using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using System;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Ready";

    private readonly DataManager _data;
    private readonly StrategyManager _strat;
    private readonly LogManager _log;
    private readonly ConfigManager _cfg;

    public MainViewModel(ConfigManager cfg, DataManager dat, StrategyManager str, LogManager log)
    {
        _cfg = cfg;
        _data = dat;
        _strat = str;
        _log = log;
    }

    [RelayCommand]
    public async Task LoadData()
    {
        // 1. Create a progress reporter that updates the UI property
        var progress = new Progress<string>(s => StatusText = s);

        // 2. Offload the data operation to a background thread
        await Task.Run(async () => 
        {
            // 3. Pass the progress reporter to the DataManager
            // Note: Replace "BTCUSD" with your actual logic to determine the symbol
            var data = await _data.GetSymbolDataAsync("BTCUSD", progress);
            
            // Logic to handle loaded data can go here
        });
    }
}