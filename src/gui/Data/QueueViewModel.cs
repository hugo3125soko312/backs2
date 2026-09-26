using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;

namespace backs2.ViewModels;

public partial class QueueRowViewModel : ObservableObject
{
    public string Symbol { get; }
    public string Timeframe { get; }
    public string Provider { get; }

    [ObservableProperty]
    private bool _checked;

    public bool PendingRemoval => !Checked;

    public QueueRowViewModel(QueuedDataset dataset)
    {
        Symbol = dataset.Symbol;
        Timeframe = dataset.Timeframe;
        Provider = dataset.Provider;
        _checked = true;
    }

    partial void OnCheckedChanged(bool value) => OnPropertyChanged(nameof(PendingRemoval));
}

public partial class QueueViewModel : ObservableObject
{
    private readonly SelectionQueue _queue;

    public ObservableCollection<QueueRowViewModel> Items { get; } = new();

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _pendingRemovalCount;

    public bool Committed { get; private set; }

    public event EventHandler? CloseRequested;

    public QueueViewModel(SelectionQueue queue)
    {
        _queue = queue;

        foreach (var item in queue.Snapshot())
        {
            var row = new QueueRowViewModel(item);
            row.PropertyChanged += (_, _) => RecomputePending();
            Items.Add(row);
        }

        RecomputePending();
    }

    private void RecomputePending()
    {
        int n = 0;
        foreach (var row in Items)
            if (row.PendingRemoval) n++;

        PendingRemovalCount = n;
        StatusText = Items.Count == 0
            ? "Queue is empty."
            : n == 0
                ? $"{Items.Count} dataset(s) queued."
                : $"{Items.Count} dataset(s) queued — {n} will be removed on Done.";
    }

    [RelayCommand]
    private void Done()
    {
        Committed = true;

        foreach (var row in Items)
        {
            if (row.PendingRemoval)
                _queue.Remove(row.Symbol, row.Timeframe);
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Committed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}