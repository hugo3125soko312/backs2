using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;

namespace backs2.ViewModels;

public partial class ConfigViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly LogManager _log;

    public ObservableCollection<string> ProviderOrder { get; } = new();

    public ObservableCollection<string> AvailableProviders { get; } = new()
    {
        "Yahoo", "Massive"
    };

    [ObservableProperty] private int _maxThreads;
    [ObservableProperty] private int _maxRetries;
    [ObservableProperty] private int _timeoutSeconds;
    [ObservableProperty] private string _selectedAvailable = "";
    [ObservableProperty] private string? _selectedInOrder;

    public ConfigViewModel(ConfigManager config, LogManager log)
    {
        _config = config;
        _log = log;

        MaxThreads = _config.Settings.MaxThreads;
        MaxRetries = _config.Settings.MaxRetries;
        TimeoutSeconds = _config.Settings.TimeoutSeconds;

        foreach (var p in _config.Settings.ProviderOrder)
            ProviderOrder.Add(p);
    }

    [RelayCommand]
    private void AddProvider()
    {
        if (string.IsNullOrWhiteSpace(SelectedAvailable)) return;
        if (ProviderOrder.Contains(SelectedAvailable)) return;

        ProviderOrder.Add(SelectedAvailable);
        SelectedAvailable = "";
    }

    [RelayCommand]
    private void RemoveProvider()
    {
        if (SelectedInOrder == null) return;
        ProviderOrder.Remove(SelectedInOrder);
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedInOrder == null) return;
        var i = ProviderOrder.IndexOf(SelectedInOrder);
        if (i <= 0) return;
        ProviderOrder.Move(i, i - 1);
        SelectedInOrder = ProviderOrder[i - 1];
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedInOrder == null) return;
        var i = ProviderOrder.IndexOf(SelectedInOrder);
        if (i < 0 || i >= ProviderOrder.Count - 1) return;
        ProviderOrder.Move(i, i + 1);
        SelectedInOrder = ProviderOrder[i + 1];
    }

    [RelayCommand]
    private void Save()
    {
        _config.Settings.MaxThreads = MaxThreads;
        _config.Settings.MaxRetries = MaxRetries;
        _config.Settings.TimeoutSeconds = TimeoutSeconds;
        _config.Settings.ProviderOrder = new System.Collections.Generic.List<string>(ProviderOrder);

        _config.SaveConfig();
        _log.Info("ConfigManager: settings saved.");
    }
}