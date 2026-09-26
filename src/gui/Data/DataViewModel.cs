using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;

namespace backs2.ViewModels;

public partial class DataViewModel : ObservableObject
{
    private readonly DataManager _dataManager;
    private readonly SelectionQueue _queue;
    private readonly List<DataRowViewModel> _all = new();

    public ObservableCollection<DataRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _filterSymbol = "";
    [ObservableProperty] private string _filterTier = "";
    [ObservableProperty] private string _filterProvider = "";
    [ObservableProperty] private string _filterSize = "";
    [ObservableProperty] private string _filterModified = "";
    [ObservableProperty] private string _filterPath = "";

    [ObservableProperty] private string _sortColumn = "Symbol";
    [ObservableProperty] private bool _sortAscending = true;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _queuedCount;

    public string SymbolSortGlyph   => GlyphFor("Symbol");
    public string TimeframeSortGlyph => GlyphFor("Timeframe");
    public string TierSortGlyph     => GlyphFor("Tier");
    public string ProviderSortGlyph => GlyphFor("Provider");
    public string SizeSortGlyph     => GlyphFor("SizeBytes");
    public string ModifiedSortGlyph => GlyphFor("LastModifiedUtc");
    public string PathSortGlyph     => GlyphFor("Path");

    public bool HasFilterSymbol   => !string.IsNullOrWhiteSpace(FilterSymbol);
    public bool HasFilterTier     => !string.IsNullOrWhiteSpace(FilterTier);
    public bool HasFilterProvider => !string.IsNullOrWhiteSpace(FilterProvider);
    public bool HasFilterSize     => !string.IsNullOrWhiteSpace(FilterSize);
    public bool HasFilterModified => !string.IsNullOrWhiteSpace(FilterModified);
    public bool HasFilterPath     => !string.IsNullOrWhiteSpace(FilterPath);

    public DataViewModel(DataManager dataManager, SelectionQueue queue)
    {
        _dataManager = dataManager;
        _queue = queue;

        _queue.Changed += OnQueueChanged;

        RefreshData();
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        QueuedCount = _queue.Items.Count;

        foreach (var row in _all)
            row.RefreshQueuedState();
    }

    [RelayCommand]
    private void RefreshData()
    {
        try
        {
            _all.Clear();
            foreach (var entry in _dataManager.GetAvailableData())
                _all.Add(new DataRowViewModel(entry, _queue));

            QueuedCount = _queue.Items.Count;
            ApplyView();
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return;

        if (string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase))
            SortAscending = !SortAscending;
        else
        {
            SortColumn = column;
            SortAscending = true;
        }

        ApplyView();
    }

    public event EventHandler? OpenQueueRequested;

    [RelayCommand]
    private void OpenQueue()
    {
        OpenQueueRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFilterSymbolChanged(string value)   { OnPropertyChanged(nameof(HasFilterSymbol));   ApplyView(); }
    partial void OnFilterTierChanged(string value)     { OnPropertyChanged(nameof(HasFilterTier));     ApplyView(); }
    partial void OnFilterProviderChanged(string value) { OnPropertyChanged(nameof(HasFilterProvider)); ApplyView(); }
    partial void OnFilterSizeChanged(string value)     { OnPropertyChanged(nameof(HasFilterSize));     ApplyView(); }
    partial void OnFilterModifiedChanged(string value) { OnPropertyChanged(nameof(HasFilterModified)); ApplyView(); }
    partial void OnFilterPathChanged(string value)     { OnPropertyChanged(nameof(HasFilterPath));     ApplyView(); }

    partial void OnSortColumnChanged(string value) => RaiseSortGlyphs();
    partial void OnSortAscendingChanged(bool value) => RaiseSortGlyphs();

    private void RaiseSortGlyphs()
    {
        OnPropertyChanged(nameof(SymbolSortGlyph));
        OnPropertyChanged(nameof(TimeframeSortGlyph));
        OnPropertyChanged(nameof(TierSortGlyph));
        OnPropertyChanged(nameof(ProviderSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(ModifiedSortGlyph));
        OnPropertyChanged(nameof(PathSortGlyph));
    }

    private string GlyphFor(string column)
    {
        if (!string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase))
            return "";
        return SortAscending ? "▲" : "▼";
    }

    private void ApplyView()
    {
        IEnumerable<DataRowViewModel> query = _all;

        if (!string.IsNullOrWhiteSpace(FilterSymbol))
            query = query.Where(e => Contains(e.Symbol, FilterSymbol));
        if (!string.IsNullOrWhiteSpace(FilterTier))
            query = query.Where(e => Contains(e.Tier, FilterTier));
        if (!string.IsNullOrWhiteSpace(FilterProvider))
            query = query.Where(e => Contains(e.Provider, FilterProvider));
        if (!string.IsNullOrWhiteSpace(FilterSize))
            query = query.Where(e => Contains(e.SizeBytes.ToString(), FilterSize));
        if (!string.IsNullOrWhiteSpace(FilterModified))
            query = query.Where(e => Contains(e.LastModifiedUtc.ToString("yyyy-MM-dd HH:mm"), FilterModified));
        if (!string.IsNullOrWhiteSpace(FilterPath))
            query = query.Where(e => Contains(e.Path, FilterPath));

        query = SortColumn switch
        {
            "Timeframe"       => SortAscending ? query.OrderBy(e => e.Timeframe)       : query.OrderByDescending(e => e.Timeframe),
            "Tier"            => SortAscending ? query.OrderBy(e => e.Tier)            : query.OrderByDescending(e => e.Tier),
            "Provider"        => SortAscending ? query.OrderBy(e => e.Provider)        : query.OrderByDescending(e => e.Provider),
            "SizeBytes"       => SortAscending ? query.OrderBy(e => e.SizeBytes)       : query.OrderByDescending(e => e.SizeBytes),
            "LastModifiedUtc" => SortAscending ? query.OrderBy(e => e.LastModifiedUtc) : query.OrderByDescending(e => e.LastModifiedUtc),
            "Path"            => SortAscending ? query.OrderBy(e => e.Path)            : query.OrderByDescending(e => e.Path),
            _                 => SortAscending ? query.OrderBy(e => e.Symbol)          : query.OrderByDescending(e => e.Symbol),
        };

        Rows.Clear();
        foreach (var row in query)
            Rows.Add(row);

        StatusText = $"{Rows.Count} of {_all.Count} dataset(s) shown — {QueuedCount} queued.";
    }

    private static bool Contains(string haystack, string needle)
        => haystack?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}