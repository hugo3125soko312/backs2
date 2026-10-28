using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Models;
using backs2.Core.MT5;
using backs2.Core.Providers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel : ObservableObject, INavigationTarget
{
    public const int MaxGridRows = 100_000;

    // -- Common Services ----------------------------------------------
    private readonly Mt5TickService _service;
    private readonly ConfigManager _config;
    private readonly CacheManager _cache;
    private readonly backs2.Core.Data.ITickCompiler _tickCompiler;
    private CancellationTokenSource? _cts;

    public string[] Timeframes => TimeframeMap.All;

    public RangeObservableCollection<Mt5Tick> Ticks { get; } = new();

    // -- Subtab selection (driven by the shell navigation tree) ------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExtractionPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsRequesterPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsTkcPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsParsingPanelVisible))]
    private int _selectedSubtabIndex;

    public bool IsExtractionPanelVisible => SelectedSubtabIndex == 0;
    public bool IsRequesterPanelVisible  => SelectedSubtabIndex == 1;
    public bool IsTkcPanelVisible        => SelectedSubtabIndex == 2;
    public bool IsParsingPanelVisible    => SelectedSubtabIndex == 3;

    public void Navigate(int subtabIndex) => SelectedSubtabIndex = subtabIndex;

    // -- Global Busy State --------------------------------------------
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingCommand))]
    [NotifyCanExecuteChangedFor(nameof(FetchTicksCommand))]
    [NotifyCanExecuteChangedFor(nameof(GetTerminalInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyRangeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyToFetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ParseCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractToCacheCommand))]
    [NotifyCanExecuteChangedFor(nameof(RequestSyncCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isFetching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isDecoding;

    // -- Common Connection Display -----------------------------------
    [ObservableProperty] private string _statusText = "Not connected.";
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private int    _latencyMs;
    [ObservableProperty] private string _connectionInfoText = "";

    public Mt5TickViewModel(Mt5TickService service, ConfigManager config, CacheManager cache, backs2.Core.Data.ITickCompiler tickCompiler)
    {
        _service = service;
        _config = config;
        _cache = cache;
        _tickCompiler = tickCompiler;
        ConnectionInfoText = $"Server listening on {_config.Settings.Mt5Host}:{_config.Settings.Mt5Port}";

        TerminalDataRoot = _config.Settings.Mt5TerminalDataRoot ?? "";
        TerminalBroker   = _config.Settings.Mt5TerminalBroker ?? "";
    }

    private bool CanRunIdle() => !IsBusy;
    private bool CanStop() => IsFetching || IsDecoding;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task Ping()
    {
        IsBusy = true;
        StatusText = "Pinging...";
        try
        {
            var result = await _service.PingAsync(CancellationToken.None);
            if (result.Success)
            {
                IsConnected = true;
                LatencyMs   = result.LatencyMs;
                StatusText  = $"Connected - {result.LatencyMs} ms";
            }
            else
            {
                IsConnected = false;
                LatencyMs   = 0;
                StatusText  = $"Disconnected - {result.Error}";
            }
        }
        finally { IsBusy = false; }
    }
}