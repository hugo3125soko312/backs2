using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.MT5;
using backs2.Core.Writers;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel : ObservableObject
{
    // ── Fetch settings (all in the tab, nothing in the MT5 script) ──
    [ObservableProperty] private string _symbol = "GOLD.pro";
    [ObservableProperty] private int    _lookbackHours = 24;
    [ObservableProperty] private int    _targetTicks = 5000;

    // Batch size per TCP request. The EA (v1.08) loops CopyTicks internally
    // to satisfy this — the MQL5 2000-tick-per-call cap no longer binds the
    // C# side. Larger = fewer round trips, chunkier UI updates.
    [ObservableProperty] private int    _chunkSize = 20000;

    // ── Display state ───────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "Not connected.";
    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private int    _latencyMs;
    [ObservableProperty] private double _fetchProgress;
    [ObservableProperty] private string _fetchProgressText = "";
    [ObservableProperty] private string _fetchEtaText = "";
    [ObservableProperty] private string _fetchSpeedText = "";
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

    public RangeObservableCollection<Mt5Tick> Ticks { get; } = new();

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
        FetchEtaText = "";
        FetchSpeedText = "";
        StatusText = "Opening stream writer...";

        _cts = new CancellationTokenSource();

        long fromMsc = LookbackHours <= 0
            ? 0
            : DateTimeOffset.UtcNow.AddHours(-LookbackHours).ToUnixTimeMilliseconds();

        Mt5TickStreamWriter? writer = null;
        Channel<IReadOnlyList<Mt5Tick>>? channel = null;
        Task? flushTask = null;

        try
        {
            writer = await Mt5TickStreamWriter.OpenAsync(Symbol);
            var localWriter = writer;

            // Bounded channel decouples the socket read loop from disk I/O.
            // 64 slots × ~20k ticks × 52 B ≈ 66 MB worst case, but in
            // practice the reader keeps up and the queue never fills.
            channel = Channel.CreateBounded<IReadOnlyList<Mt5Tick>>(
                new BoundedChannelOptions(64)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true
                });
            var localChannel = channel;

            flushTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var batch in localChannel.Reader.ReadAllAsync())
                        await localWriter.AppendAsync(batch);
                }
                catch (Exception ex)
                {
                    LogManager.Error(LogTag.Provider, "MT5: CSV flusher task failed", ex);
                }
            });

            StatusText = "Fetching ticks...";

            var progress = new Progress<Mt5FetchProgress>(p =>
            {
                FetchProgress = p.Percent;
                FetchProgressText = $"{p.Fetched} / {p.Total}";
                FetchEtaText = p.EtaSeconds > 0
                    ? $"ETA {FormatEta(p.EtaSeconds)}"
                    : "ETA —";
                FetchSpeedText = p.TicksPerSecond > 0
                    ? $"{p.TicksPerSecond:F0} t/s"
                    : "";
                StatusText = p.TotalChunks > 0
                    ? $"{p.Phase} — chunk {p.CurrentChunk}/{p.TotalChunks}"
                    : p.Phase;

                if (p.NewTicks != null && p.NewTicks.Count > 0)
                    Ticks.AddRange(p.NewTicks);
            });

            var ct = _cts.Token;
            await _service.FetchTicksAsync(
                Symbol, fromMsc, TargetTicks, ChunkSize, progress, ct,
                onBatch: async batch => await localChannel.Writer.WriteAsync(batch, ct));

            localChannel.Writer.Complete();
            await flushTask;
            flushTask = null;
            await writer.CompleteAsync();

            FetchProgress = 100;
            FetchProgressText = $"{Ticks.Count} / {Ticks.Count}";
            FetchEtaText = "";
            FetchSpeedText = "";
            StatusText = $"Loaded {Ticks.Count} ticks → {writer.Path}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Fetch cancelled.";
            await DrainAndCloseAsync(channel, flushTask, writer);
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5: fetch failed", ex);
            StatusText = $"Error: {ex.Message}";
            await DrainAndCloseAsync(channel, flushTask, writer);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsFetching = false;
            IsBusy = false;
        }
    }

    private static async Task DrainAndCloseAsync(
        Channel<IReadOnlyList<Mt5Tick>>? channel,
        Task? flushTask,
        Mt5TickStreamWriter? writer)
    {
        try { channel?.Writer.TryComplete(); } catch { }
        if (flushTask != null)
        {
            try { await flushTask; } catch { }
        }
        if (writer != null)
        {
            try { await writer.CompleteAsync(); } catch { }
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

    private static string FormatEta(double seconds)
    {
        if (seconds < 1)     return "<1s";
        if (seconds < 60)    return $"{seconds:F0}s";
        if (seconds < 3600)  return $"{seconds / 60.0:F1}m";
        return $"{seconds / 3600.0:F1}h";
    }
}