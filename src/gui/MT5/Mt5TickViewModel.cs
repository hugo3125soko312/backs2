using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.MT5;
using backs2.Core.Writers;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel : ObservableObject
{
    // ── Fetch settings (all in the tab, nothing in the MT5 script) ──
    [ObservableProperty] private string _symbol = "GOLD.pro";
    [ObservableProperty] private int    _lookbackHours = 24;
    [ObservableProperty] private int    _targetTicks = 5000;
    [ObservableProperty] private int    _chunkSize = 250;

    // ── Display state ───────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "Not connected.";
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private int    _latencyMs;
    [ObservableProperty] private double _fetchProgress;
    [ObservableProperty] private string _fetchProgressText = "";
    [ObservableProperty] private string _terminalInfoText = "";
    [ObservableProperty] private string _connectionInfoText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingCommand))]
    [NotifyCanExecuteChangedFor(nameof(FetchTicksCommand))]
    [NotifyCanExecuteChangedFor(nameof(GetTerminalInfoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCsvCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isFetching;

    public ObservableCollection<Mt5Tick> Ticks { get; } = new();

    private readonly Mt5TickService _service;
    private readonly ConfigManager _config;
    private CancellationTokenSource? _cts;

    public Mt5TickViewModel(Mt5TickService service, ConfigManager config)
    {
        _service = service;
        _config = config;
        ConnectionInfoText = $"Server listening on {_config.Settings.Mt5Host}:{_config.Settings.Mt5Port}";
    }

    private bool CanRunIdle() => !IsBusy;

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
                StatusText  = $"Connected — {result.LatencyMs} ms";
            }
            else
            {
                IsConnected = false;
                LatencyMs   = 0;
                StatusText  = $"Disconnected — {result.Error}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task FetchTicks()
    {
        IsBusy = true;
        IsFetching = true;
        Ticks.Clear();
        FetchProgress = 0;
        FetchProgressText = "";
        StatusText = "Fetching ticks...";

        _cts = new CancellationTokenSource();

        long fromMsc = LookbackHours <= 0
            ? 0
            : DateTimeOffset.UtcNow.AddHours(-LookbackHours).ToUnixTimeMilliseconds();

        var progress = new Progress<Mt5FetchProgress>(p =>
        {
            if (p.Total > 0)
                FetchProgress = Math.Min(100.0, (double)p.Fetched / p.Total * 100.0);
            FetchProgressText = $"{p.Fetched} / {p.Total}";
            StatusText = $"{p.Phase} — {p.Fetched} ticks";

            if (p.NewTicks != null)
            {
                foreach (var t in p.NewTicks)
                    Ticks.Add(t);
            }
        });

        try
        {
            await _service.FetchTicksAsync(
                Symbol, fromMsc, TargetTicks, ChunkSize, progress, _cts.Token);

            FetchProgress = 100;
            FetchProgressText = $"{Ticks.Count} / {Ticks.Count}";
            StatusText = $"Loaded {Ticks.Count} ticks";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Fetch cancelled.";
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: fetch failed", ex);
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsFetching = false;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task GetTerminalInfo()
    {
        IsBusy = true;
        TerminalInfoText = "Loading...";
        try
        {
            var info = await _service.GetTerminalInfoAsync(CancellationToken.None);
            if (info == null)
            {
                TerminalInfoText = "Failed to retrieve terminal info.";
                return;
            }

            TerminalInfoText =
                $"Company:     {info.Company}\n" +
                $"Account:     {info.Account}\n" +
                $"Server:      {info.Server}\n" +
                $"Currency:    {info.Currency}\n" +
                $"Leverage:    1:{info.Leverage}\n" +
                $"Server time: {info.ServerTimeUtc:yyyy-MM-dd HH:mm:ss} UTC";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task SaveCsv()
    {
        if (Ticks.Count == 0)
        {
            StatusText = "No ticks to save.";
            return;
        }

        IsBusy = true;
        try
        {
            var path = await Mt5TickWriter.WriteAsync(Symbol, Ticks);
            StatusText = $"Saved {Ticks.Count} ticks to {path}";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStop() => IsFetching;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();
}