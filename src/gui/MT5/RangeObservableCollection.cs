using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace backs2.ViewModels;

/// <summary>
/// ObservableCollection that supports bulk AddRange via a single Reset
/// notification. Replaces N per-item CollectionChanged events with one,
/// which is what makes 100k+ row DataGrid streaming usable.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public RangeObservableCollection() { }
    public RangeObservableCollection(IEnumerable<T> items) : base(items) { }

    public void AddRange(IReadOnlyList<T> items)
    {
        if (items == null || items.Count == 0) return;

        // Bypass per-item events by writing directly to the backing list.
        for (int i = 0; i < items.Count; i++)
            Items.Add(items[i]);

        // One notification for the whole batch.
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }
}