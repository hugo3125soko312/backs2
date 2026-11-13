using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core.Accounts;

namespace backs2.ViewModels;

using backs2.Core;
using backs2.ViewModels.Messages;
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
        _service.Initialized += OnServiceInitialized;
        _service.ActiveChanged += OnActiveChanged;
    }

    private void OnServiceInitialized(object? sender, EventArgs e)
    {
        _service.Initialized -= OnServiceInitialized;
        System.Windows.Application.Current.Dispatcher.Invoke(() => { RefreshList(); UpdateActiveBanner(); });
    }

    private void OnActiveChanged(object? sender, AccountDescriptor? descriptor)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(UpdateActiveBanner);
    }

    private void UpdateActiveBanner()
    {
        var a = _service.Active;
        ActiveAccountName = a == null ? "(none)" : $"{a.Firm} · {a.Plan} · {a.Size}";
    }

    private void RefreshList()
    {
        try
        {
            // TODO: Implement account loading
            Accounts.Clear();
            StatusText = "Accounts not yet loaded";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    partial void OnSelectedAccountChanged(AccountDescriptor? value)
    {
        HasSelection = value != null;
        if (value == null)
        {
            DetailHeader = "Select an account";
            DetailSubHeader = "";
            Notes = "";
            return;
        }

        DetailHeader = $"{value.Firm} · {value.Plan} · {value.Size}";
        DetailSubHeader = $"Firm: {value.Firm} | Plan: {value.Plan} | Size: {value.Size}";
        

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
        if (SelectedAccount == null) return;

        try
        {
            _service.SetActive(SelectedAccount);
            StatusText = $"Set active: {SelectedAccount.Firm} {SelectedAccount.Plan}";
        }
        catch (Exception ex)
        {
            StatusText = $"Set active failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = _service.RootDirectory;
        if (System.IO.Directory.Exists(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
