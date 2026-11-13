using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Accounts;
using backs2.ViewModels.Messages;
using backs2.Core.Overview;

namespace backs2.ViewModels;

public partial class MonteCarloViewModel : ObservableObject
{
    private readonly DataManager _data;
    private readonly AccountService _accountService;
    private readonly ConfigManager _config;

    public MonteCarloViewModel(DataManager data, AccountService accountService, ConfigManager config)
    {
        _data = data;
        _accountService = accountService;
        _config = config;

        AvailableAccounts = new ObservableCollection<AccountDescriptor>(_accountService.LoadAll());

        if (AvailableAccounts.Count > 0)
            SelectedAccount = AvailableAccounts[0];
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _curveMemoryLabel = "";
    [ObservableProperty] private string _sourceInfo = "No dataset loaded.";

    [ObservableProperty] private ObservableCollection<AccountDescriptor> AvailableAccounts { get; } = new();
    [ObservableProperty] private AccountDescriptor? SelectedAccount;
    [ObservableProperty] private bool _hasResults;

    [ObservableProperty] private ObservableCollection<McBatchRow> BatchResults { get; } = new();
    [ObservableProperty] private McBatchRow? SelectedBatchItem;
    [ObservableProperty] private bool HasBatch;
    [ObservableProperty] private ObservableCollection<McKpiCard> Kpis { get; } = new();
    [ObservableProperty] private ObservableCollection<McStatSection> Stats { get; } = new();

    partial void OnSelectedAccountChanged(AccountDescriptor? value)
    {
        if (value != null)
        {
            _data.ResetAccount(value.Path);
            _data.ScanForResults(value.Path);
            _sourceInfo = $"{value.Firm} · {value.Plan} · {value.Size} · {value.Path}";

            if (!string.IsNullOrWhiteSpace(value.Notes))
                _sourceInfo += $"\n{value.Notes}";

            OnPropertyChanged(nameof(SourceInfo));

            if (_hasResults)
            {
                Run();
            }
        }
    }

    [RelayCommand]
    private void Run() => ExecuteRun();

    [RelayCommand]
    private void Cancel()
    {
        // Cancel via CancelTokenSource if available
    }

    [RelayCommand]
    private void TestWorker()
    {
        // Test worker command
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
        {
            StatusText = "Monte Carlo running...";
        }
        else
        {
            StatusText = "Ready.";
        }
    }

    partial void OnProgressChanged(int value)
    {
        CurveMemoryLabel = $"Memory: {value} MB";
    }

    public string ActiveAccountName => SelectedAccount?.Firm ?? "(none)";

    private void ExecuteRun()
    {
        // Implementation would go here
    }

    private void ApplyAccountDefaults()
    {
        // Apply defaults if needed
    }
}

public sealed record McBatchRow(string Display, string PRuinText, string PFundedText, string PTimeoutText);
public sealed record McKpiCard(string Label, string Value, string Subtitle, System.Windows.Media.SolidColorBrush ValueBrush);
public sealed record McStatSection(string Header, ObservableCollection<McStatLine> Lines);
public sealed record McStatLine(string Label, string Value, System.Windows.Media.SolidColorBrush ValueBrush);
public sealed record PlotModeOption(string Label);
