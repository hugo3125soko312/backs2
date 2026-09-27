// src/gui/Providers/ProvidersViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Providers;

namespace backs2.ViewModels;

public partial class ProviderDatasetRow
{
    public string Dataset { get; }
    public string Volume { get; }
    public string Start { get; }
    public string End { get; }
    public string Span { get; }
    public string Timeframes { get; }

    public ProviderDatasetRow(CacheMetadata m)
    {
        Dataset = $"{m.Symbol}@{m.Timeframe}";
        Volume = m.HasVolume ? "Yes" : "No";
        Start = m.MinT > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.MinT).ToString("yyyy-MM-dd") : "—";
        End = m.MaxT > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.MaxT).ToString("yyyy-MM-dd") : "—";
        Span = FormatSpan(m.MinT, m.MaxT);
        Timeframes = m.Timeframe;
    }

    internal static string FormatSpan(long minT, long maxT)
    {
        if (minT <= 0 || maxT <= 0 || maxT <= minT) return "—";

        var delta = TimeSpan.FromMilliseconds(maxT - minT);
        return FormatDuration(delta.TotalDays);
    }

    internal static string FormatDuration(double days)
    {
        if (days <= 0) return "—";

        if (days < 1.0)
        {
            double hours = days * 24.0;
            if (hours < 1.0) return $"{hours * 60.0:0} min";
            return $"{hours:0.#} hours";
        }

        if (days < 60.0) return $"{days:0.#} days";

        double months = days / 30.4375;
        if (months < 24.0) return $"{months:0.#} months";

        double years = days / 365.25;
        return $"{years:0.#} years";
    }
}

public partial class ProviderGroupViewModel : ObservableObject
{
    public string Provider { get; }
    public int DatasetCount { get; }
    public string Volume { get; }
    public string Start { get; }
    public string End { get; }
    public string AverageSpan { get; }
    public string Timeframes { get; }

    public ObservableCollection<ProviderDatasetRow> Datasets { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    public string ToggleGlyph => IsExpanded ? "▼" : "▶";

    public ProviderGroupViewModel(
        string provider,
        List<CacheMetadata> metas,
        int volumeCount,
        long minT,
        long maxT)
    {
        Provider = provider;
        DatasetCount = metas.Count;
        Volume = volumeCount == 0 ? "No"
               : volumeCount == metas.Count ? "Yes"
               : $"Mixed ({volumeCount}/{metas.Count})";
        Start = minT > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(minT).ToString("yyyy-MM-dd") : "—";
        End = maxT > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(maxT).ToString("yyyy-MM-dd") : "—";
        AverageSpan = ComputeAverageSpan(metas);

        var tfs = metas.Select(m => m.Timeframe).Distinct().ToList();
        var sortedTfs = TimeframeMap.All.Where(tfs.Contains).ToList();
        sortedTfs.AddRange(tfs.Except(TimeframeMap.All).OrderBy(x => x));
        Timeframes = string.Join(", ", sortedTfs);

        foreach (var m in metas.OrderBy(m => m.Symbol).ThenBy(m => m.Timeframe))
            Datasets.Add(new ProviderDatasetRow(m));
    }

    private static string ComputeAverageSpan(List<CacheMetadata> metas)
    {
        double totalDays = 0.0;
        int contributing = 0;

        foreach (var m in metas)
        {
            if (m.MinT <= 0 || m.MaxT <= 0 || m.MaxT <= m.MinT) continue;
            totalDays += (m.MaxT - m.MinT) / 86_400_000.0;
            contributing++;
        }

        if (contributing == 0) return "—";
        return ProviderDatasetRow.FormatDuration(totalDays / contributing);
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ToggleGlyph));

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}

public partial class ProvidersViewModel : ObservableObject
{
    private readonly CacheManager _cache;

    public ObservableCollection<ProviderGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    private string _statusText = "Ready";

    public ProvidersViewModel(CacheManager cache)
    {
        _cache = cache;
        RefreshCommand.Execute(null);
    }

    [RelayCommand]
    private void Refresh()
    {
        Groups.Clear();

        var dir = "data/cache";
        if (!Directory.Exists(dir))
        {
            StatusText = "No cache directory found.";
            return;
        }

        var files = Directory.GetFiles(dir, "*.json");
        var metas = new List<CacheMetadata>(files.Length);

        foreach (var f in files)
        {
            var m = _cache.ReadCacheMetadata(f);
            if (m != null) metas.Add(m);
        }

        var grouped = metas.GroupBy(m => m.Source).OrderBy(g => g.Key);

        foreach (var g in grouped)
        {
            var list = g.ToList();
            int volCount = list.Count(m => m.HasVolume);

            long minT = long.MaxValue;
            long maxT = long.MinValue;
            foreach (var m in list)
            {
                if (m.MinT > 0 && m.MinT < minT) minT = m.MinT;
                if (m.MaxT > 0 && m.MaxT > maxT) maxT = m.MaxT;
            }
            if (minT == long.MaxValue) minT = 0;
            if (maxT == long.MinValue) maxT = 0;

            Groups.Add(new ProviderGroupViewModel(g.Key, list, volCount, minT, maxT));
        }

        StatusText = $"Aggregated stats for {Groups.Count} provider(s) across {files.Length} cached dataset(s).";
    }
}