using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core;

namespace backs2.ViewModels;

public partial class DataRowViewModel : ObservableObject
{
    private readonly SelectionQueue _queue;
    private bool _suppressWrite;

    public string Symbol { get; }
    public string Timeframe { get; }
    public string Tier { get; }
    public string Provider { get; }
    public string SizeDisplay { get; }
    public long SizeBytes { get; }
    public System.DateTime LastModifiedUtc { get; }
    public string Path { get; }
    public bool HasVolume { get; }

    [ObservableProperty]
    private bool _isQueued;

    public DataRowViewModel(DataEntry entry, SelectionQueue queue)
    {
        _queue = queue;

        Symbol = entry.Symbol;
        Timeframe = entry.Timeframe;
        Tier = entry.Tier;
        Provider = entry.Provider;
        SizeDisplay = entry.SizeDisplay;
        SizeBytes = entry.SizeBytes;
        LastModifiedUtc = entry.LastModifiedUtc;
        Path = entry.Path;
        HasVolume = entry.HasVolume;

        _suppressWrite = true;
        IsQueued = queue.Contains(Symbol, Timeframe);
        _suppressWrite = false;
    }

    partial void OnIsQueuedChanged(bool value)
    {
        if (_suppressWrite) return;

        if (value) _queue.Add(Symbol, Timeframe, Provider);
        else _queue.Remove(Symbol, Timeframe);
    }

    public void RefreshQueuedState()
    {
        _suppressWrite = true;
        IsQueued = _queue.Contains(Symbol, Timeframe);
        _suppressWrite = false;
    }
}