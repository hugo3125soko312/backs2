using System;
using System.Collections.ObjectModel;
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

    public AccountViewModel(AccountService service)
    {
        _service = service;

        if (_service.IsInitialized)
        {
            RefreshList();
            UpdateActiveBanner();
        }
        else
        {
            _service.Initialized += OnServiceInitialized;
        }

        _service.ActiveChanged += OnActiveChanged;
    }

    private void OnServiceInitialized(object? sender, EventArgs e)
    {
        _service.Initialized -= OnServiceInitialized;
        Dispatcher.UIThread.Post(() => { RefreshList(); UpdateActiveBanner(); });
    }

    private void OnActiveChanged(object? sender, AccountDescriptor? descriptor)
    {
        Dispatcher.UIThread.Post(UpdateActiveBanner);
    }

    private void UpdateActiveBanner()
    {
        var a = _service.Active;
        ActiveAccountName = a == null ? "(none)" : $"{a.Firm} · {a.Plan} · {a.Size}";
    }

    private void RefreshList()
    {
        var prevStem = SelectedAccount?.FileStem;
        var prevPlan = SelectedAccount?.Plan;
        var prevSize = SelectedAccount?.Size;

        Accounts.Clear();
        foreach (var d in _service.Descriptors)
            Accounts.Add(d);

        if (prevStem != null && prevPlan != null && prevSize != null)
        {
            foreach (var d in Accounts)
            {
                if (string.Equals(d.FileStem, prevStem, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.Plan, prevPlan, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.Size, prevSize, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedAccount = d;
                    return;
                }
            }
        }

        var active = _service.Active;
        if (active != null)
        {
            foreach (var d in Accounts)
            {
                if (string.Equals(d.FileStem, active.FileStem, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.Plan, active.Plan, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d.Size, active.Size, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedAccount = d;
                    return;
                }
            }
        }

        SelectedAccount = Accounts.Count > 0 ? Accounts[0] : null;
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
            return;
        }

        var planSize = _service.ResolvePlanSize(value);
        if (planSize == null)
        {
            PhaseRules.Clear();
            HasSelection = false;
            DetailHeader = $"{value.Firm} · {value.Plan} · {value.Size}";
            DetailSubHeader = "(failed to resolve)";
            Notes = "";
            StatusText = $"Failed to resolve {value.Firm} {value.Plan} {value.Size}";
            return;
        }

        DetailHeader = $"{value.Firm} · {value.Plan} · {value.Size}";
        DetailSubHeader = $"{value.Firm} · {value.Plan} · USD {planSize.StartingCapital:N0}";
        Notes = planSize.Notes ?? "";
        HasSelection = true;

        PhaseRules.Clear();
        foreach (var phase in planSize.Phases)
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

            if (!string.IsNullOrWhiteSpace(phase.DrawdownType))
                PhaseRules.Add(new AccountRuleRow("Drawdown Type", phase.DrawdownType));
        }

        if (!string.IsNullOrWhiteSpace(planSize.ConsistencyScoreRule))
            PhaseRules.Add(new AccountRuleRow("Consistency Score", planSize.ConsistencyScoreRule));

        StatusText = $"Loaded {value.Firm} {value.Plan} {value.Size}";
    }

    [RelayCommand]
    private void Refresh()
    {
        _service.Refresh();
        RefreshList();
        StatusText = $"Refreshed — {Accounts.Count} account entries.";
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
        StatusText = $"Active: {SelectedAccount.Firm} · {SelectedAccount.Plan} · {SelectedAccount.Size}";
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