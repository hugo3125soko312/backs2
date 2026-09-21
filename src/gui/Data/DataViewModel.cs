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
    private readonly List<DataEntry> _all = new();

    public ObservableCollection<DataEntry> Rows { get; } = new();

    // Filter inputs — one per column.
    [ObservableProperty] private string _filterSymbol = "";
    [ObservableProperty] private string _filterSource = "";
    [ObservableProperty] private string _filterSize = "";
    [ObservableProperty] private string _filterModified = "";
    [ObservableProperty] private string _filterPath = "";

    // Sort state.
    [ObservableProperty] private string _sortColumn = "Symbol";
    [ObservableProperty] private bool _sortAscending = true;

    // Status bar.
    [ObservableProperty] private string _statusText = "";

    public DataViewModel(DataManager dataManager)
    {
        _dataManager = dataManager;
        RefreshData();
    }

    [RelayCommand]
    private void RefreshData()
    {
        try
        {
            _all.Clear();
            _all.AddRange(_dataManager.GetAvailableData());
            ApplyView();
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FilterSymbol = "";
        FilterSource = "";
        FilterSize = "";
        FilterModified = "";
        FilterPath = "";
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

    partial void OnFilterSymbolChanged(string value) => ApplyView();
    partial void OnFilterSourceChanged(string value) => ApplyView();
    partial void OnFilterSizeChanged(string value) => ApplyView();
    partial void OnFilterModifiedChanged(string value) => ApplyView();
    partial void OnFilterPathChanged(string value) => ApplyView();

    private void ApplyView()
    {
        IEnumerable<DataEntry> query = _all;

        if (!string.IsNullOrWhiteSpace(FilterSymbol))
            query = query.Where(e => Contains(e.Symbol, FilterSymbol));
        if (!string.IsNullOrWhiteSpace(FilterSource))
            query = query.Where(e => Contains(e.Source, FilterSource));
        if (!string.IsNullOrWhiteSpace(FilterSize))
            query = query.Where(e => Contains(e.SizeBytes.ToString(), FilterSize));
        if (!string.IsNullOrWhiteSpace(FilterModified))
            query = query.Where(e => Contains(e.LastModifiedUtc.ToString("yyyy-MM-dd HH:mm"), FilterModified));
        if (!string.IsNullOrWhiteSpace(FilterPath))
            query = query.Where(e => Contains(e.Path, FilterPath));

        query = SortColumn switch
        {
            "Source"          => SortAscending ? query.OrderBy(e => e.Source)          : query.OrderByDescending(e => e.Source),
            "SizeBytes"       => SortAscending ? query.OrderBy(e => e.SizeBytes)       : query.OrderByDescending(e => e.SizeBytes),
            "LastModifiedUtc" => SortAscending ? query.OrderBy(e => e.LastModifiedUtc) : query.OrderByDescending(e => e.LastModifiedUtc),
            "Path"            => SortAscending ? query.OrderBy(e => e.Path)            : query.OrderByDescending(e => e.Path),
            _                 => SortAscending ? query.OrderBy(e => e.Symbol)          : query.OrderByDescending(e => e.Symbol),
        };

        Rows.Clear();
        foreach (var row in query)
            Rows.Add(row);

        StatusText = $"{Rows.Count} of {_all.Count} dataset(s) shown.";
    }

    private static bool Contains(string haystack, string needle)
        => haystack?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}