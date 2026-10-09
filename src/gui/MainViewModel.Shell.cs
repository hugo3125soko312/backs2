using CommunityToolkit.Mvvm.Input;

namespace backs2.ViewModels;

/// <summary>
/// Shell-only additions to MainViewModel: navigation commands for the
/// menu bar. Each sets SelectedNode to the corresponding tree node —
/// no integer indices anywhere.
/// </summary>
public partial class MainViewModel
{
    [RelayCommand] private void NavigateToOverview()       => SelectedNode = _overviewNode;
    [RelayCommand] private void NavigateToRunner()         => SelectedNode = _runnerNode;
    [RelayCommand] private void NavigateToMonteCarlo()     => SelectedNode = _monteCarloNode;
    [RelayCommand] private void NavigateToMetrics()        => SelectedNode = _metricsNode;
    [RelayCommand] private void NavigateToAccounts()       => SelectedNode = _accountsNode;
    [RelayCommand] private void NavigateToData()           => SelectedNode = _dataNode;
    [RelayCommand] private void NavigateToDataAutomation() => SelectedNode = _dataAutomationNode;
    [RelayCommand] private void NavigateToMt5Ticks()       => SelectedNode = _mt5TicksNode;
    [RelayCommand] private void NavigateToProviders()      => SelectedNode = _providersNode;
    [RelayCommand] private void NavigateToChart()          => SelectedNode = _chartNode;
    [RelayCommand] private void NavigateToLogs()           => SelectedNode = _logsNode;
    [RelayCommand] private void NavigateToConfig()         => SelectedNode = _configNode;
}