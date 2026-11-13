// src/gui/Chart/ChartViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core;
using backs2.Core.Models;
using backs2.ViewModels.Messages;
using ScottPlot.WPF;

namespace backs2.ViewModels;

public partial class ChartViewModel : ObservableObject, IRecipient<NavigateToTradeMessage>
{
    private readonly DataManager _data;
    private readonly SelectionQueue _queue;
    private readonly backs2.Core.Data.IQueueOriginGate _queueGate;
    private readonly ChartPlotBuilder _builder = new();
    private WpfPlot? _wpfPlot;
    private bool _suppressTimeframeReload;

    // Set when a NavigateToTradeMessage arrives before the target series
    // has finished loading. Consumed by ApplyPendingNavigation after
    // OnSeriesChanged fires.
    private DateTime? _pendingNavigationTime;

    public ObservableCollection<QueuedDataset> QueuedItems { get; } = new();
    public ObservableCollection<string> AvailableTimeframes { get; } = new();

    [ObservableProperty] private QueuedDataset? _selectedDataset;
    [ObservableProperty] private string _selectedTimeframe = "1D";
    [ObservableProperty] private string _statusText = "Chart ready.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotLoading))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private bool _isProgressDeterminate;

    [ObservableProperty] private double _loadingProgress;
    [ObservableProperty] private List<Kline> _series = new();

    [ObservableProperty] private bool _isPercent;
    [ObservableProperty] private bool _isIndexedTo100;

    [ObservableProperty] private bool _hasCrosshair;
    [ObservableProperty] private string _readoutTime = "";
    [ObservableProperty] private string _readoutOpen = "";
    [ObservableProperty] private string _readoutHigh = "";
    [ObservableProperty] private string _readoutLow = "";
    [ObservableProperty] private string _readoutClose = "";
    [ObservableProperty] private string _readoutVolume = "";
    [ObservableProperty] private string _readoutChange = "";

    public bool IsNotLoading => !IsLoading;
    public bool IsProgressIndeterminate => !IsProgressDeterminate;

    public ChartViewModel(DataManager data, SelectionQueue queue, backs2.Core.Data.IQueueOriginGate queueGate)
    {
        _data = data;
        _queue = queue;
        _queueGate = queueGate;
        _queue.Changed += OnQueueChanged;
        AvailableTimeframes.AddRange(TimeframeMap.All);
    }

    public void SetPlotControl(WpfPlot plotView)
    {
        _wpfPlot = plotView;
        RebuildPlotModel();
    }

    /// <summary>
    /// Navigation entry point. Called from the Monte Carlo cluster plot.
    /// If the requested dataset is queued, selects it (and its timeframe),
    /// then sets a pending scroll target that fires once the series loads.
    /// If it is not queued, warns — the queue is the source of truth and
    /// this method does not mutate it.
    /// </summary>
    public void Receive(NavigateToTradeMessage message)
    {
        if (_queueGate.IsOrigin(message.Origin))
        {
            var ds = QueuedItems.FirstOrDefault(d => string.Equals(d.Symbol, message.Symbol, StringComparison.OrdinalIgnoreCase));
            if (ds == null)
            {
                StatusText = $"Trade {message.Symbol} not in current queue.";
                return;
            }
            if (message.Timeframe != ds.Timeframe)
            {
                StatusText = $"Timeframe mismatch: trade is {message.Timeframe}, dataset is {ds.Timeframe}.";
                return;
            }

            SelectedDataset = ds;
            _suppressTimeframeReload = true;
            SelectedTimeframe = ds.Timeframe;
            _suppressTimeframeReload = false;

            _ = LoadAsync(ds);
        }
        else
        {
            StatusText = $"Trade from '{message.Origin}' not in current queue.";
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(SyncQueue);
    }

    private void SyncQueue()
    {
        QueuedItems.Clear();
        foreach (var q in _queue.Items) QueuedItems.Add(q);
        StatusText = $"{QueuedItems.Count} datasets queued.";
    }

    partial void OnSelectedDatasetChanged(QueuedDataset? value)
    {
        if (value == null) return;
        RefreshAvailableTimeframes(value);
        _ = LoadAsync(value);
    }

    partial void OnSelectedTimeframeChanged(string value)
    {
        if (_suppressTimeframeReload) return;
        if (SelectedDataset == null) return;
        if (string.IsNullOrWhiteSpace(value)) return;

        _ = LoadAsync(SelectedDataset);
    }

    private void RefreshAvailableTimeframes(QueuedDataset dataset)
    {
        AvailableTimeframes.Clear();
        try
        {
            var meta = _data.GetMetadata(dataset.Symbol, dataset.Timeframe);
            var cache = meta?.CachedFiles ?? Array.Empty<string>();
            foreach (var f in cache)
            {
                var tf = System.IO.Path.GetFileNameWithoutExtension(f).Split('_').LastOrDefault();
                if (!string.IsNullOrEmpty(tf)) AvailableTimeframes.Add(tf);
            }
        }
        catch
        {
            foreach (var tf in TimeframeMap.All) AvailableTimeframes.Add(tf);
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (SelectedDataset == null) return;
        await LoadAsync(SelectedDataset);
    }

    private async Task LoadAsync(QueuedDataset dataset)
    {
        try
        {
            IsLoading = true;
            LoadingProgress = 0;
            StatusText = $"Loading {dataset.Symbol}@{dataset.Timeframe}...";

            var progress = new Progress<DataProgress>(p =>
            {
                LoadingProgress = p.Percent;
                IsLoading = p.Percent < 100;
                if (p.IsDeterminate)
                {
                    StatusText = $"Loading {dataset.Symbol}@{dataset.Timeframe}... ({p.Message}) {p.Percent}%";
                }
                else
                {
                    StatusText = $"Loading {dataset.Symbol}@{dataset.Timeframe}...";
                }
            });

            var result = await _data.GetKlinesAsync(dataset.Symbol, dataset.Timeframe, dataset.Provider, progress, CancellationToken.None).ConfigureAwait(true);
            result = result ?? Array.Empty<Kline>();

            _series = result.ToList();
            if (_series.Count > 0)
            {
                RefreshAvailableTimeframes(dataset);
            }

            OnSeriesChanged(_series);

            if (result.Count() > 0)
            {
                StatusText = $"{dataset.Symbol}@{dataset.Timeframe} — {result.Count()} bars loaded.";
            }
            else
            {
                StatusText = $"No data for {dataset.Symbol}@{dataset.Timeframe}.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Load cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSeriesChanged(List<Kline> value)
    {
        RebuildPlotModel();
        ApplyPendingNavigation();
    }

    [RelayCommand]
    private void AutoScale()
    {
        if (_wpfPlot == null) return;
        _wpfPlot.Plot.Axes.AutoScale();
        _wpfPlot.Refresh();
    }

    [RelayCommand]
    private void TogglePercent()
    {
        IsPercent = !IsPercent;
        if (IsPercent) IsIndexedTo100 = false;
        RebuildPlotModel();
    }

    [RelayCommand]
    private void ToggleIndexed()
    {
        IsIndexedTo100 = !IsIndexedTo100;
        if (IsIndexedTo100) IsPercent = false;
        RebuildPlotModel();
    }

    private ChartScaleState ScaleState => new(false, IsPercent, IsIndexedTo100, false);

    private void RebuildPlotModel()
    {
        if (_wpfPlot == null) return;
        _builder.Rebuild(_wpfPlot, Series, ScaleState);
        HasCrosshair = false;
    }

    /// <summary>
    /// Scrolls the chart X-axis to a window centered on the pending target
    /// timestamp. Consumed once per navigation request.
    /// </summary>
    private void ApplyPendingNavigation()
    {
        if (_pendingNavigationTime == null) return;
        if (_wpfPlot == null) return;

        var t = _pendingNavigationTime.Value;
        var xs = _wpfPlot.Plot.GetXs(_series.Select(k => k.Timestamp).ToArray());
        int idx = NearestIndex(xs, t);
        if (idx >= 0 && idx < _series.Count)
        {
            var range = _wpfPlot.Plot.Axes.GetXRange();
            var window = (range.Max - range.Min) / 4;
            _wpfPlot.Plot.Axes.SetXLim(t.AddTicks(-(long)window.Ticks), t.AddTicks((long)window.Ticks));
        }
        _pendingNavigationTime = null;
    }

    private int NearestIndex(double[] xs, DateTime t)
    {
        if (xs.Length == 0) return -1;
        var dt = t.Ticks;
        int best = 0;
        for (int i = 1; i < xs.Length; i++)
        {
            if (Math.Abs(xs[i] - dt) < Math.Abs(xs[best] - dt)) best = i;
        }
        return best;
    }

    public void UpdateCrosshair(double dataX, double dataY)
    {
        if (_wpfPlot == null) return;
        _builder.MoveCrosshair(_wpfPlot, dataX);

        int i = NearestIndex(dataX);
        if (i >= 0 && i < _series.Count)
        {
            var k = _series[i];
            var barTs = k.Timestamp.AddTicks((long)(dataX - xs[i]));
            ReadoutTime = barTs.ToString("yyyy-MM-dd HH:mm");
            ReadoutOpen = k.Open.ToString("N2");
            ReadoutHigh = k.High.ToString("N2");
            ReadoutLow = k.Low.ToString("N2");
            ReadoutClose = k.Close.ToString("N2");
            ReadoutVolume = k.Volume.ToString("N0");
            var chg = k.Close - k.Open;
            ReadoutChange = chg >= 0 ? $"+{chg:N2}" : $"{chg:N2}";
            HasCrosshair = true;
        }
    }

    public void ClearCrosshair()
    {
        HasCrosshair = false;
        if (_wpfPlot != null) _builder.HideCrosshair(_wpfPlot);
    }

    private int NearestIndex(double dataX)
    {
        return NearestIndex(_wpfPlot?.Plot.GetXs(_series.Select(k => k.Timestamp).ToArray()) ?? Array.Empty<double>(), dataX);
    }
}
