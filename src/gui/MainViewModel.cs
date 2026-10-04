using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core;
using backs2.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;
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

    private readonly IServiceProvider _sp;
    private readonly DataManager _data;
    private readonly StrategyManager _strat;
    private readonly ConfigManager _cfg;

    // Lazily-resolved tab ViewModels. Each getter resolves once on first
    // access, which only happens when CurrentVm selects it.
    private OverviewViewModel? _overviewVm;
    private RunnerViewModel? _runnerVm;
    private MonteCarloViewModel? _monteCarloVm;
    private MetricsViewModel? _metricsVm;
    private AccountViewModel? _accountVm;
    private DataViewModel? _dataVm;
    private DataAutomationViewModel? _dataAutomationVm;
    private Mt5TickViewModel? _mt5TickVm;
    private ProvidersViewModel? _providersVm;
    private ChartViewModel? _chartVm;
    private LogsViewModel? _logsVm;
    private ConfigViewModel? _configVm;

    public OverviewViewModel OverviewVm => _overviewVm ??= _sp.GetRequiredService<OverviewViewModel>();
    public RunnerViewModel RunnerVm => _runnerVm ??= _sp.GetRequiredService<RunnerViewModel>();
    public MonteCarloViewModel MonteCarloVm => _monteCarloVm ??= _sp.GetRequiredService<MonteCarloViewModel>();
    public MetricsViewModel MetricsVm => _metricsVm ??= _sp.GetRequiredService<MetricsViewModel>();
    public AccountViewModel AccountVm => _accountVm ??= _sp.GetRequiredService<AccountViewModel>();
    public DataViewModel DataVm => _dataVm ??= _sp.GetRequiredService<DataViewModel>();
    public DataAutomationViewModel DataAutomationVm => _dataAutomationVm ??= _sp.GetRequiredService<DataAutomationViewModel>();
    public Mt5TickViewModel Mt5TickVm => _mt5TickVm ??= _sp.GetRequiredService<Mt5TickViewModel>();
    public ProvidersViewModel ProvidersVm => _providersVm ??= _sp.GetRequiredService<ProvidersViewModel>();
    public ChartViewModel ChartVm => _chartVm ??= _sp.GetRequiredService<ChartViewModel>();
    public LogsViewModel LogsVm => _logsVm ??= _sp.GetRequiredService<LogsViewModel>();
    public ConfigViewModel ConfigVm => _configVm ??= _sp.GetRequiredService<ConfigViewModel>();

    /// <summary>
    /// The single ViewModel currently hosted by MainWindow's ContentControl.
    /// Only the selected tab's ViewModel is ever constructed — this is the
    /// sole binding that touches the per-tab properties above.
    /// </summary>
    public object? CurrentVm => ActiveTabIndex switch
    {
        0  => OverviewVm,
        1  => RunnerVm,
        2  => MonteCarloVm,
        3  => MetricsVm,
        4  => AccountVm,
        5  => DataVm,
        6  => DataAutomationVm,
        7  => Mt5TickVm,
        8  => ProvidersVm,
        9  => ChartVm,
        10 => LogsVm,
        11 => ConfigVm,
        _  => null
    };

    partial void OnActiveTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentVm));
    }

    public MainViewModel(IServiceProvider sp, ConfigManager cfg, DataManager dat, StrategyManager str)
    {
        _sp = sp;
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