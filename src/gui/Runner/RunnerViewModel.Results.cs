using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    public ObservableCollection<OptimizationResultRow> OptimizationResults { get; } = new();

    public IReadOnlyList<string> AvailableSortKeys { get; } = OptimizationResultRow.AvailableSortKeys;

    [ObservableProperty] private bool _isResultsExpanded;
    [ObservableProperty] private string _resultsSortKey = "EdgePercent";
    [ObservableProperty] private string _resultsStatus = "Not loaded.";
    [ObservableProperty] private int _resultsCap = 100;

    partial void OnResultsSortKeyChanged(string value) => ApplyResultsSort();

    partial void OnResultsCapChanged(int value) => ApplyResultsSort();

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
                var files = _optStore.ListBatchFiles();
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
        int batchCount = loaded.Count == 0
            ? 0
            : loaded.Select(r => r.BatchFile).Distinct().Count();

        ApplyResultsSort();
        sw.Stop();

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

        OptimizationResults.Clear();
        foreach (var row in top)
            OptimizationResults.Add(row);
    }
}