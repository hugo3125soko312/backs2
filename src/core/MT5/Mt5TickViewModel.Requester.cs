using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.MT5;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel
{
    // ── Requester tab state ───────────────────────────────────────────
    [ObservableProperty] private string _reqSymbol = "GOLD.pro";
    [ObservableProperty] private int    _reqLookbackDays = 3652; // Default 10 years
    [ObservableProperty] private double _reqProgress;
    [ObservableProperty] private string _reqStatusText = "Ready to request sync.";

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task RequestSync()
    {
        string symbol = ReqSymbol?.Trim() ?? "";
        if (string.IsNullOrEmpty(symbol))
        {
            ReqStatusText = "Symbol is required.";
            return;
        }

        if (ReqLookbackDays <= 0)
        {
            ReqStatusText = "Lookback days must be greater than 0.";
            return;
        }

        IsBusy = true;
        ReqProgress = 0;
        ReqStatusText = $"Initializing MT5 sync request for {symbol} ({ReqLookbackDays:N0} days)...";

        LogManager.Info(LogTag.Provider,
            $"MT5 Requester: RequestSync ENTRY symbol='{symbol}' lookback={ReqLookbackDays} day(s)");

        _cts = new CancellationTokenSource();

        // Progress<T> captures SynchronizationContext at construction. This
        // method runs on the UI thread (button handler), so every Report
        // marshals back to the UI thread — the ObservableProperty setters
        // and their XAML bindings stay on the right thread.
        int frameCount = 0;
        var progress = new Progress<Mt5CountProgress>(p =>
        {
            frameCount++;
            LogManager.Debug(LogTag.Provider,
                $"MT5 Requester: progress frame #{frameCount} — " +
                $"req={p.RequestId} {p.ChunksDone}/{p.ChunksTotal} " +
                $"synced={p.SyncedDays} skipped={p.SkippedDays}");

            if (p.ChunksTotal <= 0) return;

            double pct = Math.Min(100.0, (double)p.ChunksDone / p.ChunksTotal * 100.0);
            ReqProgress = pct;
            ReqStatusText =
                $"Syncing {symbol} — day {p.ChunksDone:N0}/{p.ChunksTotal:N0} ({pct:F1}%) | " +
                $"synced {p.SyncedDays:N0}, skipped {p.SkippedDays:N0}";
        });

        try
        {
            long toMsc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long fromMsc = DateTimeOffset.UtcNow.AddDays(-ReqLookbackDays).ToUnixTimeMilliseconds();

            var result = await _service.SyncTicksRangeAsync(
                symbol, fromMsc, toMsc, progress, _cts.Token);

            ReqProgress = 100;

            if (result == null)
            {
                ReqStatusText = "Request failed — timeout, EA disconnected, or range unavailable. " +
                                "Check the Logs tab for the SYNC_TICKS_RANGE trace.";
                LogManager.Warn(LogTag.Provider,
                    $"MT5 Requester: SyncTicksRangeAsync returned null after {frameCount} progress frame(s)");
                return;
            }

            ReqStatusText =
                $"Sync Complete! Synced: {result.SyncedDays:N0} day(s) | " +
                $"Skipped: {result.SkippedDays:N0} day(s) | " +
                $"Total: {result.TotalDays:N0} day(s). " +
                $"Local .tkc files are updated and ready for extraction!";

            LogManager.Info(LogTag.Provider,
                $"MT5: Requester completed for '{symbol}'. Synced={result.SyncedDays}, " +
                $"Skipped={result.SkippedDays}, Total={result.TotalDays}, " +
                $"progress frames={frameCount}");
        }
        catch (OperationCanceledException)
        {
            ReqStatusText = "Sync request cancelled.";
            LogManager.Info(LogTag.Provider,
                $"MT5 Requester: cancelled after {frameCount} progress frame(s)");
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Provider, "MT5 Requester failed", ex);
            ReqStatusText = $"Error: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }
}