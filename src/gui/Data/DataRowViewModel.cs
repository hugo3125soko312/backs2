using System;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core;

namespace backs2.ViewModels;

public partial class DataRowViewModel : ObservableObject
{
    private readonly SelectionQueue? _queue;
    private readonly Action<DataRowViewModel, bool>? _onGroupToggle;
    private bool _suppressWrite;
    private bool _suppressGroupToggle;

    public bool IsGroupHeader { get; }
    public string GroupLabel { get; }

    public string Symbol { get; }
    public string Timeframe { get; }
    public string Tier { get; }
    public string Provider { get; }
    public string SizeDisplay { get; }
    public long SizeBytes { get; }
    public System.DateTime LastModifiedLocal { get; }
    public string Path { get; }
    public bool HasVolume { get; }
public long RangeTicks { get; }
public string RangeDisplay { get; }

    [ObservableProperty] private bool _isQueued;
    [ObservableProperty] private bool _isGroupSelected;

    public DataRowViewModel(DataEntry entry, SelectionQueue queue)
    {
        _queue = queue;
        _onGroupToggle = null;
        IsGroupHeader = false;
        GroupLabel = "";

        Symbol = entry.Symbol;
        Timeframe = entry.Timeframe;
        Tier = entry.Tier;
        Provider = entry.Provider;
        SizeDisplay = entry.SizeDisplay;
        SizeBytes = entry.SizeBytes;
        LastModifiedLocal = entry.LastModifiedLocal;
        Path = entry.Path;
        HasVolume = entry.HasVolume;
RangeTicks = (entry.MinT > 0 && entry.MaxT > entry.MinT) ? entry.MaxT - entry.MinT : 0;
RangeDisplay = RangeTicks > 0 ? ProviderDatasetRow.FormatDuration(System.TimeSpan.FromMilliseconds(RangeTicks).TotalDays) : char.ConvertFromUtf32(8212);

        _suppressWrite = true;
        IsQueued = queue.Contains(Symbol, Timeframe);
        _suppressWrite = false;
    }

    private DataRowViewModel(string symbol, int timeframeCount, Action<DataRowViewModel, bool>? onGroupToggle)
    {
        _queue = null;
        _onGroupToggle = onGroupToggle;
        IsGroupHeader = true;
        GroupLabel = $"{symbol}   ({timeframeCount})";

        Symbol = symbol;
        Timeframe = "";
        Tier = "";
        Provider = "";
        SizeDisplay = "";
        SizeBytes = 0;
        LastModifiedLocal = default;
        Path = "";
        HasVolume = false;
RangeTicks = 0;
RangeDisplay = char.ConvertFromUtf32(8212).ToString();
    }

    public static DataRowViewModel CreateGroupHeader(
        string symbol,
        int timeframeCount,
        Action<DataRowViewModel, bool>? onGroupToggle = null)
        => new(symbol, timeframeCount, onGroupToggle);

    partial void OnIsQueuedChanged(bool value)
    {
        if (_suppressWrite || _queue == null) return;

        if (value) _queue.Add(Symbol, Timeframe, Provider);
        else _queue.Remove(Symbol, Timeframe);
    }

    partial void OnIsGroupSelectedChanged(bool value)
    {
        if (!IsGroupHeader) return;
        if (_suppressGroupToggle) return;

        _onGroupToggle?.Invoke(this, value);
    }

    /// <summary>
    /// Programmatic header-state update. Bypasses the toggle callback so a
    /// state refresh (from row-level queue changes) does not re-apply the
    /// selection to every member row.
    /// </summary>
    public void SetGroupSelectedSilent(bool value)
    {
        _suppressGroupToggle = true;
        IsGroupSelected = value;
        _suppressGroupToggle = false;
    }

    public void RefreshQueuedState()
    {
        if (_queue == null) return;

        _suppressWrite = true;
        IsQueued = _queue.Contains(Symbol, Timeframe);
        _suppressWrite = false;
    }
}