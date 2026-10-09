using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core;
using backs2.ViewModels.Messages;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class MainViewModel : ObservableObject, IRecipient<NavigateToTradeMessage>
{
    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private NavigationNode? _selectedNode;

    public ObservableCollection<NavigationNode> NavigationNodes { get; } = new();

    private readonly IServiceProvider _sp;
    private readonly DataManager _data;
    private readonly StrategyManager _strat;
    private readonly ConfigManager _cfg;

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

    private NavigationNode _overviewNode       = null!;
    private NavigationNode _runnerNode         = null!;
    private NavigationNode _monteCarloNode     = null!;
    private NavigationNode _metricsNode        = null!;
    private NavigationNode _accountsNode       = null!;
    private NavigationNode _dataNode           = null!;
    private NavigationNode _dataAutomationNode = null!;
    private NavigationNode _mt5TicksNode       = null!;
    private NavigationNode _providersNode      = null!;
    private NavigationNode _chartNode          = null!;
    private NavigationNode _logsNode           = null!;
    private NavigationNode _configNode         = null!;

    public MainViewModel(IServiceProvider sp, ConfigManager cfg, DataManager dat, StrategyManager str)
    {
        _sp = sp;
        _cfg = cfg;
        _data = dat;
        _strat = str;

        BuildNavigationTree();

        WeakReferenceMessenger.Default.Register(this);

        SelectedNode = _overviewNode;

        // Kick off tab pre-warming after the initial render settles. Runs in
        // the background; cancelled by the first user selection.
        StartPrewarm();
    }

    private void BuildNavigationTree()
    {
        _overviewNode       = new NavigationNode { Header = "Overview",        Factory = () => OverviewVm };
        _runnerNode         = new NavigationNode { Header = "Runner",          SubtabIndex = 0, Factory = () => RunnerVm };
        _monteCarloNode     = new NavigationNode { Header = "Monte Carlo",     Factory = () => MonteCarloVm };
        _metricsNode        = new NavigationNode { Header = "Metrics",         Factory = () => MetricsVm };
        _accountsNode       = new NavigationNode { Header = "Accounts",        Factory = () => AccountVm };
        _dataNode           = new NavigationNode { Header = "Data",            Factory = () => DataVm };
        _dataAutomationNode = new NavigationNode { Header = "Data Automation", SubtabIndex = 0, Factory = () => DataAutomationVm };
        _mt5TicksNode       = new NavigationNode { Header = "MT5 Ticks",       SubtabIndex = 0, Factory = () => Mt5TickVm };
        _providersNode      = new NavigationNode { Header = "Providers",       Factory = () => ProvidersVm };
        _chartNode          = new NavigationNode { Header = "Chart",           Factory = () => ChartVm };
        _logsNode           = new NavigationNode { Header = "Logs",            Factory = () => LogsVm };
        _configNode         = new NavigationNode { Header = "Config",          Factory = () => ConfigVm };

        _runnerNode.Children.Add(new NavigationNode { Header = "Runner",               SubtabIndex = 0, Factory = () => RunnerVm });
        _runnerNode.Children.Add(new NavigationNode { Header = "Optimization Results", SubtabIndex = 1, Factory = () => RunnerVm });

        _dataAutomationNode.Children.Add(new NavigationNode { Header = "Extraction", SubtabIndex = 0, Factory = () => DataAutomationVm });
        _dataAutomationNode.Children.Add(new NavigationNode { Header = "Parsing",    SubtabIndex = 1, Factory = () => DataAutomationVm });
        _dataAutomationNode.Children.Add(new NavigationNode { Header = "Analysis",   SubtabIndex = 2, Factory = () => DataAutomationVm });

        _mt5TicksNode.Children.Add(new NavigationNode { Header = "Extraction",   SubtabIndex = 0, Factory = () => Mt5TickVm });
        _mt5TicksNode.Children.Add(new NavigationNode { Header = "Requester",    SubtabIndex = 1, Factory = () => Mt5TickVm });
        _mt5TicksNode.Children.Add(new NavigationNode { Header = ".tkc Extract", SubtabIndex = 2, Factory = () => Mt5TickVm });
        _mt5TicksNode.Children.Add(new NavigationNode { Header = "Parsing",      SubtabIndex = 3, Factory = () => Mt5TickVm });

        NavigationNodes.Add(_overviewNode);
        NavigationNodes.Add(_runnerNode);
        NavigationNodes.Add(_monteCarloNode);
        NavigationNodes.Add(_metricsNode);
        NavigationNodes.Add(_accountsNode);
        NavigationNodes.Add(_dataNode);
        NavigationNodes.Add(_dataAutomationNode);
        NavigationNodes.Add(_mt5TicksNode);
        NavigationNodes.Add(_providersNode);
        NavigationNodes.Add(_chartNode);
        NavigationNodes.Add(_logsNode);
        NavigationNodes.Add(_configNode);
    }

    partial void OnSelectedNodeChanged(NavigationNode? value)
    {
        // Any user navigation cancels background warming — their click wins.
        CancelPrewarmIfStarted();

        if (value?.Factory is null) return;

        var owningRoot = FindOwningRoot(value);
        if (owningRoot is null) return;

        owningRoot.Content ??= owningRoot.Factory();

        foreach (var root in NavigationNodes)
            root.IsActive = ReferenceEquals(root, owningRoot);

        if (value.SubtabIndex >= 0 && owningRoot.Content is INavigationTarget target)
            target.Navigate(value.SubtabIndex);

        ApplyAccordion(value);
    }

    private NavigationNode? FindOwningRoot(NavigationNode node)
    {
        foreach (var root in NavigationNodes)
        {
            if (ReferenceEquals(root, node) || root.Children.Contains(node))
                return root;
        }
        return null;
    }

    private void ApplyAccordion(NavigationNode selected)
    {
        NavigationNode? owningRoot = FindOwningRoot(selected);

        foreach (var root in NavigationNodes)
        {
            if (root.Children.Count == 0)
            {
                if (root.IsExpanded) root.IsExpanded = false;
                continue;
            }

            root.IsExpanded = ReferenceEquals(root, owningRoot);
        }
    }

    public void Receive(NavigateToTradeMessage message)
    {
        SelectedNode = _chartNode;
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