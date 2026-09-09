using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Ready";

    private readonly DataManager _data;
    private readonly StrategyManager _strat;

    public MainViewModel(ConfigManager cfg, DataManager dat, StrategyManager str, LogManager log)
    {
        _data = dat;
        _strat = str;
    }

    [RelayCommand]
    public async Task LoadData()
    {
        StatusText = "Downloading market data...";
        await Task.Run(() => { /* logic */ });
        StatusText = "Data load complete.";
    }
}