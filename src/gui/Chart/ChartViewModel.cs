// src\gui\Chart\ChartViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Models;
using ScottPlot.Avalonia;

namespace backs2.ViewModels;

public partial class ChartViewModel : ObservableObject
{
    private readonly DataManager _data;
    private readonly SelectionQueue _queue;
    private readonly ChartPlotBuilder _builder = new();
    private AvaPlot? _avaPlot;
    private bool _suppressTimeframeReload;

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
    }

    public void SetPlotControl(AvaPlot plotView)
    {
        _avaPlot = plotView;
        RebuildPlotModel();
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
                // No cached timeframes — fall back to the dataset's own timeframe.
                AvailableTimeframes.Add(dataset.Timeframe);
                SelectedTimeframe = dataset.Timeframe;
                return;
            }

            // Prefer the dataset's own timeframe when present.
            if (cached.Any(tf => string.Equals(tf, dataset.Timeframe, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedTimeframe = dataset.Timeframe;
                return;
            }

            // Keep the current selection if it is still valid for this symbol.
            if (cached.Any(tf => string.Equals(tf, SelectedTimeframe, StringComparison.OrdinalIgnoreCase)))
                return;

            // Otherwise pick the first cached timeframe.
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

    partial void OnSeriesChanged(List<Kline> value) => RebuildPlotModel();

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