using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using backs2.Core.Optimizer;
using backs2.Core.WalkForward;
using backs2.Core;

namespace backs2.ViewModels;

public partial class RunnerViewModel
{
    private List<WalkForwardWindowRow> _allWindowRows = new();

    private CancellationTokenSource? _wfResultsLoadCts;

    /// <summary>
    /// Bulk-notifying collection — one Reset per swap instead of N Adds.
    /// This is what keeps the DataGrid responsive when the tab is opened.
    /// </summary>
    public RangeObservableCollection<WalkForwardWindowRow> WalkForwardWindowRows { get; } = new();

    public IReadOnlyList<string> WalkForwardBatchIds { get; private set; } = Array.Empty<string>();

    public ObservableCollection<WalkForwardWindowCard> WalkForwardCards { get; } = new();
    [ObservableProperty] private bool _wfShowCards = true;
    [ObservableProperty] private string _wfVerdict = "";
    [ObservableProperty] private string _wfSummaryText = "";

    [ObservableProperty] private bool _isWalkForwardResultsExpanded;
    [ObservableProperty] private string _wfResultsSortKey = "OosNetPnl";
    [ObservableProperty] private string _wfResultsStatus = "Not loaded.";
    [ObservableProperty] private int _wfResultsCap = 200;
    [ObservableProperty] private int _wfBatchesToLoad = 20;

    partial void OnWfResultsSortKeyChanged(string value) => ApplyWfResultsSort();
    partial void OnWfResultsCapChanged(int value) => ApplyWfResultsSort();
    partial void OnWfBatchesToLoadChanged(int value)
    {
        if (!IsWalkForwardResultsExpanded) return;
        _ = RefreshWalkForwardResultsAsync();
    }

    [RelayCommand]
    private async Task RefreshWalkForwardResultsAsync()
    {
        _wfResultsLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _wfResultsLoadCts = cts;
        var ct = cts.Token;

        WfResultsStatus = "Loading walk-forward batches...";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        List<WalkForwardWindowRow> loaded;

        try
        {
            loaded = await Task.Run(() =>
            {
                var store = new WalkForwardStore();
                var files = store.ListBatchFiles()
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .Take(Math.Max(1, WfBatchesToLoad))
                    .ToList();

                var batchIds = new List<string>();
                var rows = new List<WalkForwardWindowRow>();

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    var batch = store.LoadAsync(file, ct).GetAwaiter().GetResult();
                    if (batch == null) continue;

                    batchIds.Add(Path.GetFileNameWithoutExtension(file));

                    foreach (var w in batch.Windows)
                    {
                        ct.ThrowIfCancellationRequested();
                        rows.Add(new WalkForwardWindowRow(batch, w));
                    }
                }

                WalkForwardBatchIds = batchIds;
                return rows;
            }, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            WfResultsStatus = $"Load failed: {ex.Message}";
            LogManager.Error(LogTag.Runner, "walk-forward results load failed", ex);
            return;
        }

        if (ct.IsCancellationRequested) return;

        _allWindowRows = loaded;

        // Build card view
        var cards = new List<WalkForwardWindowCard>();
        foreach (var row in loaded)
        {
            cards.Add(new WalkForwardWindowCard(row));
        }
        WalkForwardCards.Clear();
        foreach (var card in cards) WalkForwardCards.Add(card);

        ApplyWfResultsSort();
        sw.Stop();

        int batchCount = WalkForwardBatchIds.Count;
        WfResultsStatus = _allWindowRows.Count == 0
            ? $"No walk-forward batches found in {new WalkForwardStore().RootDirectory}."
            : $"Loaded {_allWindowRows.Count:N0} window(s) from {batchCount} batch file(s) " +
              $"in {sw.ElapsedMilliseconds} ms - showing top {Math.Min(WfResultsCap, _allWindowRows.Count)}.";
    }

    [RelayCommand]
    private void OpenWalkForwardFolder()
    {
        var root = new WalkForwardStore().RootDirectory;
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);

        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = root, UseShellExecute = true }); }
        catch (Exception ex)
        {
            WfResultsStatus = $"Could not open folder: {ex.Message}";
            LogManager.Error(LogTag.Runner, "could not open walk-forward folder", ex);
        }
    }

    [RelayCommand]
    private void ClearWalkForwardResults()
    {
        WalkForwardWindowRows.Clear();
        _allWindowRows = new();
        WfResultsStatus = "Cleared.";
    }

    private void ApplyWfResultsSort()
    {
        var key = WfResultsSortKey ?? "OosNetPnl";
        IEnumerable<WalkForwardWindowRow> ordered = key switch
        {
            "WindowIndex" => _allWindowRows.OrderBy(r => r.WindowIndex),
            "OosNetPnl" => _allWindowRows.OrderByDescending(r => r.OosNetPnl),
            "OosMaxDrawdown" => _allWindowRows.OrderBy(r => r.OosMaxDrawdown),
            "OosTerminalBalance" => _allWindowRows.OrderByDescending(r => r.OosTerminalBalance),
            "OosTradeCount" => _allWindowRows.OrderByDescending(r => r.OosTradeCount),
            "IsObjective" => _allWindowRows.OrderByDescending(r => r.IsObjective),
            "OosObjective" => _allWindowRows.OrderByDescending(r => r.OosObjective),
            "Degradation" => _allWindowRows.OrderBy(r => r.Degradation),
            "Objective" => _allWindowRows.OrderByDescending(r => r.OosObjective),
            _ => _allWindowRows.OrderByDescending(r => r.OosNetPnl),
        };

        WalkForwardWindowRows.Clear();
        foreach (var r in ordered.Take(Math.Max(1, WfResultsCap)))
            WalkForwardWindowRows.Add(r);
    }
}
