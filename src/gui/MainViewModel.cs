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

    /// <summary>
    /// The single ViewModel currently hosted by MainWindow's ContentControl.
    /// Set by OnSelectedNodeChanged — never bound to an index.
    /// </summary>
    [ObservableProperty]
    private object? _currentVm;

    /// <summary>
    /// Two-way bound to the TreeView's SelectedItem. Drives CurrentVm and
    /// (for child nodes) the parent VM's SelectedSubtabIndex.
    /// </summary>
    [ObservableProperty]
    private NavigationNode? _selectedNode;

    /// <summary>Root of the navigation tree. Children live on each node.</summary>
    public ObservableCollection<NavigationNode> NavigationNodes { get; } = new();

    private readonly IServiceProvider _sp;
    private readonly DataManager _data;
    private readonly StrategyManager _strat;
    private readonly ConfigManager _cfg;

    // Lazily-resolved tab ViewModels. Each getter resolves once on first
    // access — preserves the Phase 17 pattern exactly.
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

    // Node references used by menu commands and the NavigateToTradeMessage handler.
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

        // Default selection. Fires OnSelectedNodeChanged → resolves Overview VM.
        SelectedNode = _overviewNode;
    }

    private void BuildNavigationTree()
    {
        _overviewNode       = new NavigationNode { Header = "Overview",        Factory = () => OverviewVm };
        _runnerNode         = new NavigationNode { Header = "Runner",          SubtabIndex = 0, Factory = () => RunnerVm };
        _monteCarloNode     = new NavigationNode { Header = "Monte Carlo",     Factory = () => MonteCarloVm };
        _metricsNode        = new NavigationNode { Header = "Metrics",         Factory = () => MetricsVm };
        _accountsNode       = new NavigationNode { Header = "Accounts",        Factory = () => AccountVm };
        _dataNode           = new NavigationNode { Header = "Data",            Factory = () => DataVm };
        _dataAutomationNode = new NavigationNode { Header = "Data Automation", Factory = () => DataAutomationVm };
        _mt5TicksNode       = new NavigationNode { Header = "MT5 Ticks",       Factory = () => Mt5TickVm };
        _providersNode      = new NavigationNode { Header = "Providers",       Factory = () => ProvidersVm };
        _chartNode          = new NavigationNode { Header = "Chart",           Factory = () => ChartVm };
        _logsNode           = new NavigationNode { Header = "Logs",            Factory = () => LogsVm };
        _configNode         = new NavigationNode { Header = "Config",          Factory = () => ConfigVm };

        // Runner subtabs — same VM, different SubtabIndex.
        _runnerNode.Children.Add(new NavigationNode
        {
            Header = "Runner",
            SubtabIndex = 0,
            Factory = () => RunnerVm
        });
        _runnerNode.Children.Add(new NavigationNode
        {
            Header = "Optimization Results",
            SubtabIndex = 1,
            Factory = () => RunnerVm
        });

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
        if (value?.Factory is null) return;

        var vm = value.Factory();
        CurrentVm = vm;

        if (value.SubtabIndex >= 0 && vm is INavigationTarget target)
            target.Navigate(value.SubtabIndex);

        ApplyAccordion(value);
    }

    /// <summary>
    /// Expands the root that owns <paramref name="selected"/> and collapses
    /// every other root. Clicking a leaf or a child both keep exactly one
    /// root open; clicking an unrelated root collapses the previous one.
    /// </summary>
    private void ApplyAccordion(NavigationNode selected)
    {
        NavigationNode? owningRoot = null;
        foreach (var root in NavigationNodes)
        {
            if (ReferenceEquals(root, selected) || root.Children.Contains(selected))
            {
                owningRoot = root;
                break;
            }
        }

        foreach (var root in NavigationNodes)
        {
            if (root.Children.Count == 0)
            {
                // Leaf roots are never expandable — no chevron, no state.
                if (root.IsExpanded) root.IsExpanded = false;
                continue;
            }

            root.IsExpanded = ReferenceEquals(root, owningRoot);
        }
    }

    /// <summary>
    /// Handles a click on the Monte Carlo cluster plot. Switches to the
    /// Chart tab; ChartViewModel handles the dataset load and scroll via
    /// its own subscription to the same message.
    /// </summary>
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