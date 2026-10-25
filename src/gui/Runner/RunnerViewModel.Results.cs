using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Optimizer;

namespace backs2.ViewModels;

public partial class RunnerViewModel
{
    private List<OptimizationResultRow> _allResults = new();

    private CancellationTokenSource? _resultsLoadCts;

    /// <summary>
    /// Bulk-notifying collection — one Reset per swap instead of N Adds.
    /// This is what keeps the DataGrid responsive when the tab is opened.
    /// </summary>
    public RangeObservableCollection<OptimizationResultRow> OptimizationResults { get; } = new();

    public IReadOnlyList<string> AvailableSortKeys { get; } = OptimizationResultRow.AvailableSortKeys;

    [ObservableProperty] private bool _isResultsExpanded;
    [ObservableProperty] private string _resultsSortKey = "EdgePercent";
    [ObservableProperty] private string _resultsStatus = "Not loaded.";
    [ObservableProperty] private int _resultsCap = 100;

    /// <summary>
    /// How many of the most recent batch files to deserialise. Each batch holds
    /// ~500 versions, every one carrying a 1000-element terminal-balance array,
    /// so loading all 92 batches meant parsing ~370 MB of JSON to display 100
    /// rows. Newest-first by filename, which embeds the UTC timestamp.
    /// </summary>
    [ObservableProperty] private int _batchesToLoad = 10;

    partial void OnResultsSortKeyChanged(string value) => ApplyResultsSort();

    partial void OnResultsCapChanged(int value) => ApplyResultsSort();

    partial void OnBatchesToLoadChanged(int value)
    {
        if (!IsResultsExpanded) return;
        _ = RefreshResultsAsync();
    }

    [RelayCommand]
    private async Task RefreshResultsAsync()
    {
        _resultsLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resultsLoadCts = cts;
        var ct = cts.Token;

        ResultsStatus = "Loading batches...";
        var sw = Stopwatch.StartNew();

        List<OptimizationResultRow> loaded;

        try
        {
            loaded = await Task.Run(() =>
            {
                var files = _optStore.ListBatchFiles()
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .Take(Math.Max(1, BatchesToLoad))
                    .ToList();
                var rows = new List<OptimizationResultRow>();

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    var batch = _optStore.LoadAsync(file, ct).GetAwaiter().GetResult();
                    if (batch == null) continue;

                    var fileName = Path.GetFileName(file);

                    for (int i = 0; i < batch.Versions.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        rows.Add(new OptimizationResultRow(
                            batch.Id, fileName, batch.Versions[i], batch));
                    }
                }

                return rows;
            }, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ResultsStatus = $"Load failed: {ex.Message}";
            LogManager.Error(LogTag.Runner, "optimization results load failed", ex);
            return;
        }

        if (ct.IsCancellationRequested) return;

        _allResults = loaded;

        // One UI-thread swap — Clear + AddRange emits two notifications total.
        ApplyResultsSort();
        sw.Stop();

        int batchCount = loaded.Count == 0
            ? 0
            : loaded.Select(r => r.BatchFile).Distinct().Count();

        ResultsStatus = _allResults.Count == 0
            ? $"No optimization batches found in {_optStore.RootDirectory}."
            : $"Loaded {_allResults.Count:N0} version(s) from {batchCount} batch file(s) " +
              $"in {sw.ElapsedMilliseconds} ms - showing top {Math.Min(ResultsCap, _allResults.Count)}.";
    }

    [RelayCommand]
    private void OpenResultsFolder()
    {
        try
        {
            var path = Path.GetFullPath(_optStore.RootDirectory);
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Runner, "failed to open optimizations folder", ex);
            ResultsStatus = $"Open folder failed: {ex.Message}";
        }
    }

    private void ApplyResultsSort()
    {
        if (_allResults.Count == 0)
        {
            OptimizationResults.Clear();
            return;
        }

        string key = string.IsNullOrWhiteSpace(ResultsSortKey) ? "EdgePercent" : ResultsSortKey;
        bool ascending = OptimizationResultRow.IsLowerBetter(key);

        IEnumerable<OptimizationResultRow> sorted = ascending
            ? _allResults.OrderBy(r => r.SortValue(key))
                         .ThenByDescending(r => r.BatchId)
                         .ThenBy(r => r.VersionIndex)
            : _allResults.OrderByDescending(r => r.SortValue(key))
                         .ThenByDescending(r => r.BatchId)
                         .ThenBy(r => r.VersionIndex);

        var top = sorted.Take(Math.Max(1, ResultsCap)).ToList();

        // Clear fires one Reset; AddRange fires another. Two notifications
        // total regardless of row count. This is the whole fix — the previous
        // N individual Add calls each triggered a DataGrid re-measure.
        OptimizationResults.Clear();
        OptimizationResults.AddRange(top);
    }
}