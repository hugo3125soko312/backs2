using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Providers;

namespace backs2.ViewModels;

public partial class DataViewModel : ObservableObject, INavigationTarget
{
    private const string AutoProvider = "Auto";

    private readonly DataManager _dataManager;
    private readonly CacheManager _cacheManager;
    private readonly SelectionQueue _queue;
    private readonly backs2.Core.Data.ITickCompiler _tickCompiler;
    private readonly List<DataRowViewModel> _all = new();
    private readonly Dictionary<DataRowViewModel, List<DataRowViewModel>> _groupMembers = new();

    // Latest-wins refresh. If a fetch completes and triggers a refresh while
    // the ctor-triggered refresh is still in flight, the newer one cancels
    // the older one instead of racing it.
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _filterCts;

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
[ObservableProperty] private string _filterRange = "";

    [ObservableProperty] private backs2.Core.Data.DataOrigin _originFilter = backs2.Core.Data.DataOrigin.TimeframeFetch;

    partial void OnOriginFilterChanged(backs2.Core.Data.DataOrigin value) => ApplyView();
    [ObservableProperty] private int _selectedSubtabIndex = 0;

    public bool IsRawTicksVisible => SelectedSubtabIndex == 0;
    public bool IsTimeframesVisible => SelectedSubtabIndex == 1;

    public void Navigate(int subtabIndex) => SelectedSubtabIndex = subtabIndex;

    partial void OnSelectedSubtabIndexChanged(int value)
    {
        OriginFilter = value == 0 ? backs2.Core.Data.DataOrigin.RawTick : backs2.Core.Data.DataOrigin.TimeframeFetch;
        OnPropertyChanged(nameof(IsRawTicksVisible));
        OnPropertyChanged(nameof(IsTimeframesVisible));
    }

    [RelayCommand] private void SelectRawTicks() => SelectedSubtabIndex = 0;
    [RelayCommand] private void SelectTimeframes() => SelectedSubtabIndex = 1;
    [ObservableProperty] private DataRowViewModel? _selectedRow;

    public string CompileSourceLabel =>
        SelectedRow == null ? "select one"
        : string.Equals(SelectedRow.Tier, "Ticks (CSV)", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFileName(SelectedRow.Path)
            : "select a tick CSV row";

    [ObservableProperty] private string _compileTimeframe = "1h";

    public string[] CompileTimeframeOptions { get; } = new[] { "1m", "5m", "15m", "30m", "1h", "4h", "1D", "1W" };
    [ObservableProperty] private double _compileProgress;
    [ObservableProperty] private bool _isCompiling;
    private string? _pendingOverwrite;
    [ObservableProperty] private string _compileStatus = "";

    partial void OnSelectedRowChanged(DataRowViewModel? value) => OnPropertyChanged(nameof(CompileSourceLabel));

    [RelayCommand]
    private async Task Compile() => await CompileInternal(queueAfter: false);

    [RelayCommand]
    private async Task CompileAndQueue() => await CompileInternal(queueAfter: true);

    private async Task CompileInternal(bool queueAfter)
    {
        if (IsCompiling) return;
        if (SelectedRow == null || !string.Equals(SelectedRow.Tier, "Ticks (CSV)", StringComparison.OrdinalIgnoreCase))
        {
            CompileStatus = "Select a tick CSV row in the grid first.";
            return;
        }
        var csvPath = SelectedRow.Path;
        if (!System.IO.File.Exists(csvPath))
        {
            CompileStatus = "CSV file no longer exists.";
            return;
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(csvPath);
        var symbolPrefix = stem.Split('_')[0];
        var cachePath = "data/cache/" + symbolPrefix + "_" + CompileTimeframe + ".json";
        var overwriteKey = symbolPrefix + "@" + CompileTimeframe;
        bool isExistingCompile = false;
        if (System.IO.File.Exists(cachePath))
        {
            try
            {
                var existingSrc = _cacheManager.ReadSourceFromCacheFile(cachePath);
                isExistingCompile = string.Equals(existingSrc, "MT5 Parsed", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }
        if (isExistingCompile && _pendingOverwrite != overwriteKey)
        {
            _pendingOverwrite = overwriteKey;
            CompileStatus = "WARNING: an MT5-compiled " + overwriteKey + " already exists. Click Compile again to overwrite.";
            return;
        }
        _pendingOverwrite = null;
        IsCompiling = true;
        CompileProgress = 0;
        CompileStatus = "Compiling...";
        try
        {
            var progress = new Progress<backs2.Core.Data.TickCompileProgress>(p =>
            {
                CompileProgress = p.Percent;
                CompileStatus = p.Message;
            });
            var result = await _tickCompiler.CompileAsync(csvPath, CompileTimeframe, progress).ConfigureAwait(true);
            CompileProgress = 100;
            CompileStatus = $"Compiled {result.Bars:N0} bars: {result.Symbol}@{result.Timeframe}.";
            if (queueAfter)
            {
                _queue.Add(result.Symbol, result.Timeframe, "MT5 Parsed", backs2.Core.Data.DataOrigin.RawTick);
            }
            await RefreshData();
        }
        catch (Exception ex)
        {
            CompileStatus = "Compile failed: " + ex.Message;
            LogManager.Error(LogTag.Data, "Compile from tick CSV failed", ex);
        }
        finally
        {
            IsCompiling = false;
        }
    }

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
public string RangeSortGlyph => GlyphFor("RangeTicks");

    public DataViewModel(DataManager dataManager, SelectionQueue queue, ConfigManager config, backs2.Core.Data.ITickCompiler tickCompiler, CacheManager cacheManager)
    {
        _dataManager = dataManager;
        _queue = queue;

        _queue.Changed += OnQueueChanged;

        foreach (var tf in TimeframeMap.All)
            AvailableTimeframes.Add(tf);

        AvailableProviders.Add(AutoProvider);
        foreach (var p in config.Settings.ProviderOrder)
            AvailableProviders.Add(p);

        // Fire-and-forget: the tab renders immediately with "Scanning cache…"
        // and the grid populates when the metadata scan completes. The
        // continuation lands on the UI thread via ConfigureAwait(true) inside
        // RefreshDataAsync.
        _ = RefreshData();
        _tickCompiler = tickCompiler;
        _cacheManager = cacheManager;
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
    private Task RefreshData() => RefreshDataAsync();

    private async Task RefreshDataAsync()
    {
        var prev = _refreshCts;
        _refreshCts = new CancellationTokenSource();
        prev?.Cancel();

        var ct = _refreshCts.Token;

        var sw = Stopwatch.StartNew();
        try
        {
            StatusText = "Scanning cache…";

            var entries = await _dataManager.GetAvailableDataAsync(ct).ConfigureAwait(true);
            long scanMs = sw.ElapsedMilliseconds;
            if (ct.IsCancellationRequested) return;

            _all.Clear();
            foreach (var entry in entries)
                _all.Add(new DataRowViewModel(entry, _queue));

            LogManager.Info(LogTag.Data,
                $"[18B] Data tab refresh: scan {scanMs} ms, {entries.Count} dataset(s), {sw.ElapsedMilliseconds} ms total");

            QueuedCount = _queue.Items.Count;
            ApplyView();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh — nothing to do.
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

            // Awaited: the cache listing must reflect the just-written file
            // before we resolve its provider below. Cancels any in-flight
            // ctor refresh via the CTS.
            await RefreshData();

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

    partial void OnFilterSymbolChanged(string value)    => ScheduleApplyView();
    partial void OnFilterTimeframeChanged(string value) => ScheduleApplyView();
    partial void OnFilterTierChanged(string value)      => ScheduleApplyView();
    partial void OnFilterProviderChanged(string value)  => ScheduleApplyView();
    partial void OnFilterSizeChanged(string value)      => ScheduleApplyView();
    partial void OnFilterModifiedChanged(string value)  => ScheduleApplyView();
    partial void OnFilterPathChanged(string value)      => ScheduleApplyView();

    /// <summary>
    /// Coalesces filter keystrokes into one rebuild. ApplyView clears and
    /// repopulates Rows, firing a collection-changed notification per row, so
    /// running it on every character made typing a 6-character filter trigger
    /// six full grid rebuilds.
    /// </summary>
    private void ScheduleApplyView()
    {
        var prev = _filterCts;
        _filterCts = new CancellationTokenSource();
        prev?.Cancel();
        var ct = _filterCts.Token;

        _ = Task.Delay(150, ct).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyView);
        }, TaskScheduler.Default);
    }

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
OnPropertyChanged(nameof(RangeSortGlyph));
    }

    private string GlyphFor(string column)
    {
        if (!string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase))
            return "";
        return SortAscending ? "▲" : "▼";
    }

    private void ApplyView()
    {
        IEnumerable<DataRowViewModel> query = _all
            .Where(e => e.Origin == OriginFilter);

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

        if (!string.IsNullOrWhiteSpace(FilterRange))
query = query.Where(e => Contains(e.RangeDisplay, FilterRange));

var groupedRows = query
            .GroupBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Symbol = g.Key,
                Rows = OrderWithinGroup(g).ToList()
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

    partial void OnFilterRangeChanged(string value) => ScheduleApplyView();

    private IOrderedEnumerable<DataRowViewModel> OrderWithinGroup(
        IGrouping<string, DataRowViewModel> g)
    {
        string col = SortColumn ?? "Symbol"; if (string.Equals(col, "Timeframe", StringComparison.OrdinalIgnoreCase)) return SortAscending ? g.OrderBy(r => TimeframeOrder(r.Timeframe)).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase) : g.OrderByDescending(r => TimeframeOrder(r.Timeframe)).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase); if (string.Equals(col, "Tier", StringComparison.OrdinalIgnoreCase)) return SortAscending ? g.OrderBy(r => r.Tier, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase) : g.OrderByDescending(r => r.Tier, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase); if (string.Equals(col, "Provider", StringComparison.OrdinalIgnoreCase)) return SortAscending ? g.OrderBy(r => r.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase) : g.OrderByDescending(r => r.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase); if (string.Equals(col, "HasVolume", StringComparison.OrdinalIgnoreCase)) return SortAscending ? g.OrderBy(r => r.HasVolume).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase) : g.OrderByDescending(r => r.HasVolume).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase); if (string.Equals(col, "SizeBytes", StringComparison.OrdinalIgnoreCase)) return SortAscending ? g.OrderBy(r => r.SizeBytes).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase) : g.OrderByDescending(r => r.SizeBytes).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase); if (string.Equals(col, "LastModifiedLocal", StringComparison.OrdinalIgnoreCase)) return SortAscending ? g.OrderBy(r => r.LastModifiedLocal).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase) : g.OrderByDescending(r => r.LastModifiedLocal).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase); if (string.Equals(col, "RangeTicks", StringComparison.OrdinalIgnoreCase))
            return SortAscending
                ? g.OrderBy(r => r.RangeTicks).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                : g.OrderByDescending(r => r.RangeTicks).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase);

        if (string.Equals(col, "Path", StringComparison.OrdinalIgnoreCase)) return SortAscending ? g.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase) : g.OrderByDescending(r => r.Path, StringComparer.OrdinalIgnoreCase); return g.OrderByDescending(r => TimeframeOrder(r.Timeframe))
                .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase);
    }

private static bool Contains(string haystack, string needle)
        => haystack?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}