using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;

namespace backs2.ViewModels;

public partial class ConfigViewModel : ObservableObject
{
    private readonly ConfigManager _config;

    [ObservableProperty]
    private int _maxThreads;

    [ObservableProperty]
    private string _yahooApiKey = string.Empty;

    [ObservableProperty]
    private int _maxRetries;

    [ObservableProperty]
    private int _timeoutSeconds;

    [ObservableProperty]
    private string _activeProviderKey = string.Empty;

    public ConfigViewModel(ConfigManager config)
    {
        _config = config;
        MaxThreads = _config.Settings.MaxThreads;
        YahooApiKey = _config.Settings.YahooApiKey;
        MaxRetries = _config.Settings.MaxRetries;
        TimeoutSeconds = _config.Settings.TimeoutSeconds;
        ActiveProviderKey = _config.Settings.ActiveProviderKey;
    }

    [RelayCommand]
    public void Save()
    {
        _config.Settings.MaxThreads = MaxThreads;
        _config.Settings.YahooApiKey = YahooApiKey;
        _config.Settings.MaxRetries = MaxRetries;
        _config.Settings.TimeoutSeconds = TimeoutSeconds;
        _config.Settings.ActiveProviderKey = ActiveProviderKey;
        
        _config.SaveConfig();
    }
}