using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core.Accounts;

namespace backs2.ViewModels;

public sealed record AccountRuleRow(string Label, string Value);

public partial class AccountViewModel : ObservableObject
{
    private readonly AccountService _service;

    public ObservableCollection<AccountDescriptor> Accounts { get; } = new();
    public ObservableCollection<AccountRuleRow> PhaseRules { get; } = new();

    [ObservableProperty] private AccountDescriptor? _selectedAccount;
    [ObservableProperty] private string _activeAccountName = "(none)";
    [ObservableProperty] private string _detailHeader = "Select an account";
    [ObservableProperty] private string _detailSubHeader = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _hasSelection;

    private AccountConfig? _loadedConfig;
    private string _loadedStem = "";

    public AccountViewModel(AccountService service)
    {
        _service = service;

        RefreshList();
        UpdateActiveBanner();

        _service.ActiveChanged += OnActiveChanged;
    }

    private void OnActiveChanged(object? sender, AccountConfig? account)
    {
        Dispatcher.UIThread.Post(UpdateActiveBanner);
    }

    private void UpdateActiveBanner()
    {
        ActiveAccountName = _service.Active?.Name ?? "(none)";
    }

    private void RefreshList()
    {
        var previousStem = SelectedAccount?.FileStem;

        Accounts.Clear();
        foreach (var d in _service.Descriptors)
            Accounts.Add(d);

        if (previousStem != null)
        {
            foreach (var d in Accounts)
            {
                if (string.Equals(d.FileStem, previousStem, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedAccount = d;
                    break;
                }
            }
        }
        else
        {
            var activeStem = _service.Active != null
                ? Accounts.FirstOrDefault(a =>
                    string.Equals(a.FileStem, _service.Active.Name.Replace(" ", "_"),
                        StringComparison.OrdinalIgnoreCase))?.FileStem
                : null;

            SelectedAccount = Accounts.FirstOrDefault();
        }
    }

    partial void OnSelectedAccountChanged(AccountDescriptor? value)
    {
        if (value == null)
        {
            PhaseRules.Clear();
            HasSelection = false;
            DetailHeader = "Select an account";
            DetailSubHeader = "";
            Notes = "";
            _loadedConfig = null;
            _loadedStem = "";
            return;
        }

        _ = LoadAccountAsync(value);
    }

    private System.Threading.Tasks.Task LoadAccountAsync(AccountDescriptor descriptor)
    {
        var config = _service.Load(descriptor);
        if (config == null)
        {
            PhaseRules.Clear();
            HasSelection = false;
            DetailHeader = descriptor.FileStem;
            DetailSubHeader = "(failed to load)";
            Notes = "";
            StatusText = $"Failed to load {descriptor.FileStem}";
            _loadedConfig = null;
            _loadedStem = "";
            return System.Threading.Tasks.Task.CompletedTask;
        }

        _loadedConfig = config;
        _loadedStem = descriptor.FileStem;

        DetailHeader = config.Name;
        DetailSubHeader = $"{config.Firm} · {config.Plan} · {config.Currency} {config.StartingCapital:N0}";
        Notes = config.Notes ?? "";
        HasSelection = true;

        PhaseRules.Clear();
        foreach (var phase in config.Phases)
        {
            PhaseRules.Add(new AccountRuleRow("— " + phase.PhaseName + " —", ""));

            if (phase.ProfitTargetPercent.HasValue)
                PhaseRules.Add(new AccountRuleRow("Profit Target", $"{phase.ProfitTargetPercent.Value:0.##}%"));

            if (phase.MaxLossPercent.HasValue)
                PhaseRules.Add(new AccountRuleRow("Max Loss", $"{phase.MaxLossPercent.Value:0.##}%"));

            if (phase.MaxDailyLossPercent.HasValue)
                PhaseRules.Add(new AccountRuleRow("Max Daily Loss", $"{phase.MaxDailyLossPercent.Value:0.##}%"));

            if (phase.MinTradingDays.HasValue)
                PhaseRules.Add(new AccountRuleRow("Min Trading Days", phase.MinTradingDays.Value.ToString()));

            if (phase.TradingPeriodDays.HasValue)
                PhaseRules.Add(new AccountRuleRow("Trading Period", $"{phase.TradingPeriodDays.Value} days"));

            if (phase.BestDayRulePercent.HasValue)
                PhaseRules.Add(new AccountRuleRow("Best Day Rule", $"{phase.BestDayRulePercent.Value:0.##}%"));

            if (phase.ProfitSplitPercent.HasValue)
                PhaseRules.Add(new AccountRuleRow("Profit Split", $"{phase.ProfitSplitPercent.Value:0.##}%"));

            if (phase.Refundable.HasValue)
                PhaseRules.Add(new AccountRuleRow("Refundable", phase.Refundable.Value ? "Yes" : "No"));
        }

        if (!string.IsNullOrWhiteSpace(config.ConsistencyScoreRule))
            PhaseRules.Add(new AccountRuleRow("Consistency Score", config.ConsistencyScoreRule));

        StatusText = $"Loaded {config.Name}";
        return System.Threading.Tasks.Task.CompletedTask;
    }

    [RelayCommand]
    private void Refresh()
    {
        _service.Refresh();
        RefreshList();
        StatusText = $"Refreshed — {Accounts.Count} accounts.";
    }

    [RelayCommand]
    private void SetActive()
    {
        if (SelectedAccount == null)
        {
            StatusText = "No account selected.";
            return;
        }

        _service.SetActive(SelectedAccount);
        StatusText = $"Active: {_loadedConfig?.Name ?? SelectedAccount.FileStem}";
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var path = System.IO.Path.GetFullPath("data/accounts");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Open folder failed: {ex.Message}";
        }
    }
}