using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core;
using backs2.ViewModels.Messages;
using System;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class MainViewModel : ObservableObject, IRecipient<NavigateToTradeMessage>
{
    [ObservableProperty]
    private string _statusText = "Ready";

    // Bound to TabControl.SelectedIndex in MainWindow.axaml.
    // 0=Overview, 1=Runner, 2=Monte Carlo, 3=Metrics, 4=Accounts, 5=Data,
    // 6=Data Automation, 7=MT5 Ticks, 8=Providers, 9=Chart, 10=Logs, 11=Config.
    [ObservableProperty]
    private int _activeTabIndex;

    private readonly DataManager _data;
    private readonly StrategyManager _strat;
    private readonly ConfigManager _cfg;

    public MainViewModel(ConfigManager cfg, DataManager dat, StrategyManager str)
    {
        _cfg = cfg;
        _data = dat;
        _strat = str;

        WeakReferenceMessenger.Default.Register(this);
    }

    /// <summary>
    /// Handles a click on the Monte Carlo cluster plot. Switches to the
    /// Chart tab; ChartViewModel handles the dataset load and scroll via
    /// its own subscription to the same message.
    /// </summary>
    public void Receive(NavigateToTradeMessage message)
    {
        // 9 is Chart in MainWindow's TabControl order.
        ActiveTabIndex = 9;
    }

    [RelayCommand]
    public async Task LoadData()
    {
        var progress = new Progress<string>(s => StatusText = s);

        await Task.Run(async () =>
        {
            var data = await _data.GetSymbolDataAsync("BTCUSD", progress);
        });
    }
}