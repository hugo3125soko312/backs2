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

    /// <summary>Status-bar performance chips, one per metric, coloured independently.</summary>
    public ObservableCollection<PerfChip> PerfChips { get; } = new()
    {
        new PerfChip { Label = "FPS" },
        new PerfChip { Label = "RAM" },
        new PerfChip { Label = "CPU" },
        new PerfChip { Label = "VRAM" },
        new PerfChip { Label = "GPU" }
    };

    private readonly IServiceProvider _sp;
    private readonly DataManager _data;
    private readonly StrategyManager _strat;
    private readonly ConfigManager _cfg;
    private readonly backs2.Core.Diagnostics.PerfSampler _perf;
    private readonly backs2.Core.Diagnostics.VramProbe _vram = new();
    private readonly backs2.Core.Diagnostics.NvmlProbe _nvml = new();
    private readonly backs2.Core.Diagnostics.PerfThresholds _perfThresholds = new();
    private bool _perfStarted;

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

    public MainViewModel(IServiceProvider sp, ConfigManager cfg, DataManager dat, StrategyManager str, backs2.Core.Diagnostics.PerfSampler perf)
    {
        _sp = sp;
        _cfg = cfg;
        _data = dat;
        _strat = str;
        _perf = perf;
        _perf.Sampled += OnPerfSampled;
        _perf.SustainedBreach += OnPerfBreach;

        BuildNavigationTree();

        WeakReferenceMessenger.Default.Register(this);

        SelectedNode = _overviewNode;

        // Kick off tab pre-warming after the initial render settles. Runs in
        // the background; cancelled by the first user selection.
        StartPrewarm();
    }

    /// <summary>
    /// Starts the performance sampler and the frame counter. Called from
    /// MainWindow.Opened - the window is realised there, so the compositor
    /// exists and RequestAnimationFrame is safe. Starting this from
    /// OnFrameworkInitializationCompleted crashed the process, which is why
    /// it lives here and why every step is guarded.
    /// </summary>
    public void StartPerf(Avalonia.Controls.Window window)
    {
        if (_perfStarted) return;
        _perfStarted = true;

        try
        {
            _perf.DeviceProbe = () =>
            {
                var (used, budget) = _vram.Query();
                return (used, budget, _nvml.Query());
            };

            _perf.Start();
        }
        catch (Exception ex)
        {
            LogManager.Warn(LogTag.App, "perf sampler failed to start: " + ex.Message);
        }

        try
        {
            Action<TimeSpan>? frame = null;
            frame = _ =>
            {
                _perf.OnFrame();

                if (window.IsActive)
                {
                    window.RequestAnimationFrame(frame!);
                }
                else
                {
                    // Cap at ~10 FPS when unfocused. Re-requesting
                    // unconditionally forced the compositor to render
                    // continuously for a window nobody was looking at, which is
                    // both wasted GPU time and a distorted FPS reading.
                    Avalonia.Threading.DispatcherTimer.RunOnce(
                        () => window.RequestAnimationFrame(frame!),
                        TimeSpan.FromMilliseconds(100));
                }
            };
            window.RequestAnimationFrame(frame);
        }
        catch (Exception ex)
        {
            LogManager.Warn(LogTag.App, "perf frame loop failed: " + ex.Message);
        }
    }

    private void OnPerfSampled(backs2.Core.Diagnostics.PerfSnapshot s)
    {
        var b = _perfThresholds.Breaches(s);

        SetChip(0, s.Fps.HasValue ? s.Fps.Value.ToString("F0") : "-", b, "fps");
        SetChip(1, s.AvailableRamMb.ToString("F0") + "M", b, "ram");
        SetChip(2, s.CpuPercent.ToString("F0") + "%", b, "cpu");

        string vram = s.VramUsedMb.HasValue && s.VramBudgetMb.HasValue
            ? s.VramUsedMb.Value.ToString("F0") + "/" + s.VramBudgetMb.Value.ToString("F0") + "M"
            : "-";
        SetChip(3, vram, b, "vram");

        string gpu = s.GpuPercent.HasValue ? s.GpuPercent.Value.ToString("F0") + "%" : "-";
        SetChip(4, gpu, b, "gpu");
    }

    private void SetChip(int index, string value, string[] breaches, string key)
    {
        if (index >= PerfChips.Count) return;
        var chip = PerfChips[index];
        chip.Value = value;
        chip.Breached = Array.IndexOf(breaches, key) >= 0;
    }

    private void OnPerfBreach(backs2.Core.Diagnostics.PerfSnapshot s, System.Collections.Generic.IReadOnlyList<string> metrics)
    {
        LogManager.Warn(LogTag.App,
            "[perf] sustained breach: " + string.Join(",", metrics) +
            " during '" + StatusText + "'");
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


        _dataNode.Children.Add(new NavigationNode { Header = "Raw Ticks",  SubtabIndex = 0, Factory = () => DataVm });
        _dataNode.Children.Add(new NavigationNode { Header = "Timeframes", SubtabIndex = 1, Factory = () => DataVm });

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

        // Tab switching flips IsVisible on stacked ContentControls; the view
        // never leaves the visual tree, so Attached/DetachedFromVisualTree do
        // not fire on a switch. The selected node is the only real signal that
        // the Logs tab is on screen, and the Logs VM skips its O(2000-line)
        // LogText rebuild while it is not.
        if (_logsVm != null)
            _logsVm.SetVisible(ReferenceEquals(value, _logsNode));

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