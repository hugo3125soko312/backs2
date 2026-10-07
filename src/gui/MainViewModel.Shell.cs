using CommunityToolkit.Mvvm.Input;

namespace backs2.ViewModels;

/// <summary>
/// Shell-only additions to MainViewModel: navigation commands for the
/// menu bar and left nav rail. Nothing here touches the DI graph or
/// construction order — these are pure index setters.
/// </summary>
public partial class MainViewModel
{
    [RelayCommand] private void NavigateToOverview()     => ActiveTabIndex = 0;
    [RelayCommand] private void NavigateToRunner()       => ActiveTabIndex = 1;
    [RelayCommand] private void NavigateToMonteCarlo()   => ActiveTabIndex = 2;
    [RelayCommand] private void NavigateToMetrics()      => ActiveTabIndex = 3;
    [RelayCommand] private void NavigateToAccounts()     => ActiveTabIndex = 4;
    [RelayCommand] private void NavigateToData()         => ActiveTabIndex = 5;
    [RelayCommand] private void NavigateToDataAutomation() => ActiveTabIndex = 6;
    [RelayCommand] private void NavigateToMt5Ticks()     => ActiveTabIndex = 7;
    [RelayCommand] private void NavigateToProviders()    => ActiveTabIndex = 8;
    [RelayCommand] private void NavigateToChart()        => ActiveTabIndex = 9;
    [RelayCommand] private void NavigateToLogs()         => ActiveTabIndex = 10;
    [RelayCommand] private void NavigateToConfig()       => ActiveTabIndex = 11;
}