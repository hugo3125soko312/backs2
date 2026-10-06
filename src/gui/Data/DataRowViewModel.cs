using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core;

namespace backs2.ViewModels;

public partial class DataRowViewModel : ObservableObject
{
    private readonly SelectionQueue? _queue;
    private bool _suppressWrite;

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

    [ObservableProperty]
    private bool _isQueued;

    public DataRowViewModel(DataEntry entry, SelectionQueue queue)
    {
        _queue = queue;
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

        _suppressWrite = true;
        IsQueued = queue.Contains(Symbol, Timeframe);
        _suppressWrite = false;
    }

    private DataRowViewModel(string symbol, int timeframeCount)
    {
        _queue = null;
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
    }

    public static DataRowViewModel CreateGroupHeader(string symbol, int timeframeCount)
        => new(symbol, timeframeCount);

    partial void OnIsQueuedChanged(bool value)
    {
        if (_suppressWrite || _queue == null) return;

        if (value) _queue.Add(Symbol, Timeframe, Provider);
        else _queue.Remove(Symbol, Timeframe);
    }

    public void RefreshQueuedState()
    {
        if (_queue == null) return;

        _suppressWrite = true;
        IsQueued = _queue.Contains(Symbol, Timeframe);
        _suppressWrite = false;
    }
}