// src\gui\Chart\ChartViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core;
using backs2.Core.Models;
using backs2.ViewModels.Messages;
using ScottPlot.Avalonia;

namespace backs2.ViewModels;

public partial class ChartViewModel : ObservableObject, IRecipient<NavigateToTradeMessage>
{
    private readonly DataManager _data;
    private readonly SelectionQueue _queue;
    private readonly ChartPlotBuilder _builder = new();
    private AvaPlot? _avaPlot;
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

    public ChartViewModel(DataManager data, SelectionQueue queue)
    {
        _data = data;
        _queue = queue;

        _queue.Changed += OnQueueChanged;
        SyncQueue();

        WeakReferenceMessenger.Default.Register(this);
    }

    public void SetPlotControl(AvaPlot plotView)
    {
        _avaPlot = plotView;
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
        Dispatcher.UIThread.Post(() =>
        {
            QueuedDataset? match = null;
            foreach (var item in QueuedItems)
            {
                if (string.Equals(item.Symbol, message.Symbol, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Timeframe, message.Timeframe, StringComparison.OrdinalIgnoreCase))
                {
                    match = item;
                    break;
                }
            }

            if (match == null)
            {
                StatusText = $"Navigate requested for {message.Symbol} @ {message.Timeframe}, " +
                             $"but it is not queued. Add it in the Data tab first.";
                return;
            }

            _pendingNavigationTime = message.TargetTime;

            bool sameDataset = SelectedDataset != null &&
                string.Equals(SelectedDataset.Key, match.Key, StringComparison.OrdinalIgnoreCase);

            if (!sameDataset)
            {
                // Dataset change triggers RefreshAvailableTimeframes + LoadAsync.
                // The pending target survives until OnSeriesChanged fires.
                SelectedDataset = match;
            }
            else if (!string.Equals(SelectedTimeframe, message.Timeframe, StringComparison.OrdinalIgnoreCase))
            {
                // Same dataset, different timeframe — force a reload.
                SelectedTimeframe = message.Timeframe;
            }
            else if (Series.Count > 0)
            {
                // Series already loaded; apply immediately.
                ApplyPendingNavigation();
            }
        });
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(SyncQueue);
    }

    private void SyncQueue()
    {
        var previousKey = SelectedDataset?.Key;

        QueuedItems.Clear();
        foreach (var item in _queue.Snapshot())
            QueuedItems.Add(item);

        if (previousKey != null)
        {
            foreach (var item in QueuedItems)
            {
                if (string.Equals(item.Key, previousKey, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedDataset = item;
                    return;
                }
            }
        }

        SelectedDataset = QueuedItems.Count > 0 ? QueuedItems[0] : null;
    }

    partial void OnSelectedDatasetChanged(QueuedDataset? value)
    {
        if (value == null)
        {
            AvailableTimeframes.Clear();
            Series = new List<Kline>();
            StatusText = "Queue is empty — add data in the Data tab first.";
            return;
        }

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
        var cached = _data.GetCachedTimeframes(dataset.Symbol);

        _suppressTimeframeReload = true;
        try
        {
            AvailableTimeframes.Clear();
            foreach (var tf in cached)
                AvailableTimeframes.Add(tf);

            if (AvailableTimeframes.Count == 0)
            {
                AvailableTimeframes.Add(dataset.Timeframe);
                SelectedTimeframe = dataset.Timeframe;
                return;
            }

            if (cached.Any(tf => string.Equals(tf, dataset.Timeframe, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedTimeframe = dataset.Timeframe;
                return;
            }

            if (cached.Any(tf => string.Equals(tf, SelectedTimeframe, StringComparison.OrdinalIgnoreCase)))
                return;

            SelectedTimeframe = cached[0];
        }
        finally
        {
            _suppressTimeframeReload = false;
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
        if (IsLoading) return;

        try
        {
            IsLoading = true;
            IsProgressDeterminate = false;
            LoadingProgress = 0;

            string timeframe = string.IsNullOrWhiteSpace(SelectedTimeframe)
                ? dataset.Timeframe
                : SelectedTimeframe;

            var progress = new Progress<DataProgress>(p =>
            {
                StatusText = p.Message;
                IsProgressDeterminate = p.IsDeterminate;
                LoadingProgress = p.Percent;
            });

            var data = await _data.GetSymbolDataAsync(dataset.Symbol, timeframe, progress);

            if (data == null || data.Count == 0)
            {
                StatusText = $"No data for {dataset.Symbol} @ {timeframe}.";
                Series = new List<Kline>();
                return;
            }

            Series = data;
            StatusText = $"Loaded {data.Count} bars — {dataset.Symbol} @ {timeframe}";
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
        if (_avaPlot == null) return;
        _avaPlot.Plot.Axes.AutoScale();
        _avaPlot.Refresh();
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
        if (_avaPlot == null) return;
        _builder.Rebuild(_avaPlot, Series, ScaleState);
        HasCrosshair = false;
    }

    /// <summary>
    /// Scrolls the chart X-axis to a window centered on the pending target
    /// timestamp. Consumed once per navigation request.
    /// </summary>
    private void ApplyPendingNavigation()
    {
        if (_pendingNavigationTime == null) return;
        if (_avaPlot == null || Series.Count == 0) return;

        var target = _pendingNavigationTime.Value;
        _pendingNavigationTime = null;

        int idx = FindNearestIndexByTime(target);
        if (idx < 0) return;

        const int windowBars = 200;
        int lo = Math.Max(0, idx - windowBars / 2);
        int hi = Math.Min(Series.Count - 1, lo + windowBars - 1);
        if (hi - lo < 5) { lo = 0; hi = Series.Count - 1; }

        double xMin = Series[lo].Timestamp.ToOADate();
        double xMax = Series[hi].Timestamp.ToOADate();

        _avaPlot.Plot.Axes.SetLimitsX(xMin, xMax);
        _avaPlot.Refresh();

        StatusText = $"Jumped to {Series[idx].Timestamp:yyyy-MM-dd HH:mm}";
    }

    private int FindNearestIndexByTime(DateTime target)
    {
        if (Series.Count == 0) return -1;

        int lo = 0, hi = Series.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Series[mid].Timestamp < target) lo = mid + 1;
            else hi = mid;
        }

        if (lo > 0)
        {
            long diffLo = Math.Abs((Series[lo].Timestamp - target).Ticks);
            long diffPrev = Math.Abs((Series[lo - 1].Timestamp - target).Ticks);
            if (diffPrev < diffLo) return lo - 1;
        }
        return lo;
    }

    public void UpdateCrosshair(double dataX, double dataY)
    {
        if (Series.Count == 0)
        {
            ClearCrosshair();
            return;
        }

        int idx = NearestIndex(dataX);
        if (idx < 0)
        {
            ClearCrosshair();
            return;
        }

        var k = Series[idx];
        double prevClose = idx > 0 ? Series[idx - 1].Close : k.Open;
        double change = k.Close - prevClose;

        ReadoutTime = k.Timestamp.ToString("yyyy-MM-dd HH:mm");
        ReadoutOpen = k.Open.ToString("0.#####");
        ReadoutHigh = k.High.ToString("0.#####");
        ReadoutLow = k.Low.ToString("0.#####");
        ReadoutClose = k.Close.ToString("0.#####");
        ReadoutVolume = k.Volume.ToString("0");
        ReadoutChange = (change >= 0 ? "+" : "") + change.ToString("0.#####");

        HasCrosshair = true;

        if (_avaPlot != null)
        {
            _builder.MoveCrosshair(_avaPlot, k.Timestamp.ToOADate());
        }
    }

    public void ClearCrosshair()
    {
        HasCrosshair = false;
        if (_avaPlot != null) _builder.HideCrosshair(_avaPlot);
    }

    private int NearestIndex(double dataX)
    {
        if (Series.Count == 0) return -1;
        int lo = 0, hi = Series.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            double midX = Series[mid].Timestamp.ToOADate();
            if (midX < dataX) lo = mid + 1;
            else hi = mid;
        }

        double loX = Series[lo].Timestamp.ToOADate();
        if (lo > 0)
        {
            double prevX = Series[lo - 1].Timestamp.ToOADate();
            if (Math.Abs(prevX - dataX) < Math.Abs(loX - dataX)) return lo - 1;
        }
        return lo;
    }
}