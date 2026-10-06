using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Providers;

namespace backs2.ViewModels;

public partial class DataViewModel : ObservableObject
{
    private const string AutoProvider = "Auto";

    private readonly DataManager _dataManager;
    private readonly SelectionQueue _queue;
    private readonly List<DataRowViewModel> _all = new();
    private readonly Dictionary<DataRowViewModel, List<DataRowViewModel>> _groupMembers = new();

    public ObservableCollection<DataRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> AvailableTimeframes { get; } = new();
    public ObservableCollection<string> AvailableProviders { get; } = new();

    [ObservableProperty] private string _filterSymbol = "";
    [ObservableProperty] private string _filterTimeframe = "";
    [ObservableProperty] private string _filterTier = "";
    [ObservableProperty] private string _filterProvider = "";
    [ObservableProperty] private string _filterSize = "";
    [ObservableProperty] private string _filterModified = "";
    [ObservableProperty] private string _filterPath = "";

    [ObservableProperty] private string _sortColumn = "Symbol";
    [ObservableProperty] private bool _sortAscending = true;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _queuedCount;

    [ObservableProperty] private string _fetchSymbol = "";
    [ObservableProperty] private string _fetchTimeframe = "1D";
    [ObservableProperty] private string _fetchProvider = AutoProvider;
    [ObservableProperty] private bool _isFetching;
    [ObservableProperty] private double _fetchProgress;
    [ObservableProperty] private bool _isFetchProgressIndeterminate;
    [ObservableProperty] private string _fetchStatus = "";

    public string SymbolSortGlyph    => GlyphFor("Symbol");
    public string TimeframeSortGlyph => GlyphFor("Timeframe");
    public string TierSortGlyph      => GlyphFor("Tier");
    public string ProviderSortGlyph  => GlyphFor("Provider");
    public string SizeSortGlyph      => GlyphFor("SizeBytes");
    public string ModifiedSortGlyph  => GlyphFor("LastModifiedLocal");
    public string PathSortGlyph      => GlyphFor("Path");
    public string HasVolumeSortGlyph => GlyphFor("HasVolume");

    public DataViewModel(DataManager dataManager, SelectionQueue queue, ConfigManager config)
    {
        _dataManager = dataManager;
        _queue = queue;

        _queue.Changed += OnQueueChanged;

        foreach (var tf in TimeframeMap.All)
            AvailableTimeframes.Add(tf);

        AvailableProviders.Add(AutoProvider);
        foreach (var p in config.Settings.ProviderOrder)
            AvailableProviders.Add(p);

        RefreshData();
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        QueuedCount = _queue.Items.Count;

        foreach (var row in _all)
            row.RefreshQueuedState();

        RefreshHeaderStates();
    }

    private void RefreshHeaderStates()
    {
        foreach (var kvp in _groupMembers)
        {
            bool allSelected = kvp.Value.All(r => r.IsQueued);
            kvp.Key.SetGroupSelectedSilent(allSelected);
        }
    }

    private void OnGroupHeaderToggled(DataRowViewModel header, bool selected)
    {
        if (!_groupMembers.TryGetValue(header, out var members)) return;

        foreach (var row in members)
            row.IsQueued = selected;
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
    private Task Fetch() => FetchInternal(queueAfter: false);

    [RelayCommand]
    private Task FetchAndQueue() => FetchInternal(queueAfter: true);

    private async Task FetchInternal(bool queueAfter)
    {
        if (IsFetching) return;

        var symbol = FetchSymbol?.Trim() ?? "";
        var timeframe = string.IsNullOrWhiteSpace(FetchTimeframe) ? "1D" : FetchTimeframe;
        bool isAuto = string.Equals(FetchProvider, AutoProvider, StringComparison.Ordinal);
        string? preferred = isAuto ? null : FetchProvider;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            FetchStatus = "Enter a symbol.";
            return;
        }

        try
        {
            IsFetching = true;
            FetchProgress = 0;
            IsFetchProgressIndeterminate = false;

            string requestLabel = isAuto ? "Auto" : preferred!;
            FetchStatus = queueAfter
                ? $"Fetching {symbol}@{timeframe} via {requestLabel} for queue..."
                : $"Fetching {symbol}@{timeframe} via {requestLabel}...";

            var progress = new Progress<DataProgress>(p =>
            {
                FetchStatus = p.Message;
                IsFetchProgressIndeterminate = !p.IsDeterminate;
                FetchProgress = p.Percent;
            });

            var data = await _dataManager.GetSymbolDataAsync(symbol, timeframe, progress, preferred);

            if (data == null || data.Count == 0)
            {
                FetchStatus = $"No data for {symbol}@{timeframe}.";
                return;
            }

            RefreshData();

            string actual = ResolveProviderFromCache(symbol, timeframe);

            string viaText;
            if (isAuto)
            {
                viaText = $"via {actual}";
            }
            else if (string.Equals(actual, preferred, StringComparison.OrdinalIgnoreCase))
            {
                viaText = $"via {actual}";
            }
            else
            {
                viaText = $"via {actual} (fell back from {preferred})";
            }

            if (queueAfter)
            {
                _queue.Add(symbol, timeframe, actual);
                FetchStatus = $"Fetched and queued {symbol}@{timeframe} ({data.Count} bars, {viaText}).";
            }
            else
            {
                FetchStatus = $"Fetched {symbol}@{timeframe} ({data.Count} bars) {viaText}.";
            }
        }
        catch (Exception ex)
        {
            FetchStatus = $"Fetch failed: {ex.Message}";
        }
        finally
        {
            IsFetching = false;
            FetchProgress = 100;
            IsFetchProgressIndeterminate = false;
        }
    }

    private string ResolveProviderFromCache(string symbol, string timeframe)
    {
        var info = _dataManager.GetCacheInfo(symbol, timeframe);
        return info?.Provider ?? "Unknown";
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

    partial void OnFilterSymbolChanged(string value)    => ApplyView();
    partial void OnFilterTimeframeChanged(string value) => ApplyView();
    partial void OnFilterTierChanged(string value)      => ApplyView();
    partial void OnFilterProviderChanged(string value)  => ApplyView();
    partial void OnFilterSizeChanged(string value)      => ApplyView();
    partial void OnFilterModifiedChanged(string value)  => ApplyView();
    partial void OnFilterPathChanged(string value)      => ApplyView();

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
        OnPropertyChanged(nameof(HasVolumeSortGlyph));
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
        if (!string.IsNullOrWhiteSpace(FilterTimeframe))
            query = query.Where(e => Contains(e.Timeframe, FilterTimeframe));
        if (!string.IsNullOrWhiteSpace(FilterTier))
            query = query.Where(e => Contains(e.Tier, FilterTier));
        if (!string.IsNullOrWhiteSpace(FilterProvider))
            query = query.Where(e => Contains(e.Provider, FilterProvider));
        if (!string.IsNullOrWhiteSpace(FilterSize))
            query = query.Where(e => Contains(e.SizeBytes.ToString(), FilterSize));
        if (!string.IsNullOrWhiteSpace(FilterModified))
            query = query.Where(e => Contains(e.LastModifiedLocal.ToString("yyyy-MM-dd HH:mm"), FilterModified));
        if (!string.IsNullOrWhiteSpace(FilterPath))
            query = query.Where(e => Contains(e.Path, FilterPath));

        var groupedRows = query
            .GroupBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Symbol = g.Key,
                Rows = g
                    .OrderByDescending(r => TimeframeOrder(r.Timeframe))
                    .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });

        bool symbolSort = string.Equals(SortColumn, "Symbol", StringComparison.OrdinalIgnoreCase);
        bool descending = symbolSort && !SortAscending;

        var orderedGroups = descending
            ? groupedRows.OrderByDescending(g => g.Symbol, StringComparer.OrdinalIgnoreCase)
            : groupedRows.OrderBy(g => g.Symbol, StringComparer.OrdinalIgnoreCase);

        Rows.Clear();
        _groupMembers.Clear();

        int shownRows = 0;
        int groupCount = 0;
        foreach (var group in orderedGroups)
        {
            var header = DataRowViewModel.CreateGroupHeader(group.Symbol, group.Rows.Count, OnGroupHeaderToggled);
            _groupMembers[header] = group.Rows;
            Rows.Add(header);
            groupCount++;

            foreach (var row in group.Rows)
            {
                Rows.Add(row);
                shownRows++;
            }
        }

        RefreshHeaderStates();

        StatusText = $"{shownRows} of {_all.Count} dataset(s) shown in {groupCount} group(s) — {QueuedCount} queued.";
    }

    private static int TimeframeOrder(string timeframe)
    {
        int index = 0;
        foreach (var tf in TimeframeMap.All)
        {
            if (string.Equals(tf, timeframe, StringComparison.OrdinalIgnoreCase))
                return index;
            index++;
        }
        return -1;
    }

    private static bool Contains(string haystack, string needle)
        => haystack?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}