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

public partial class ProviderStatRow : ObservableObject
{
    public string Provider { get; }
    public int DatasetCount { get; }
    public string Timeframes { get; }
    public string VolumeStatus { get; }
    public string EarliestDate { get; }
    public string LatestDate { get; }

    public ProviderStatRow(string provider, int count, IEnumerable<string> tfs, int volCount, long minT, long maxT)
    {
        Provider = provider;
        DatasetCount = count;

        var sortedTfs = TimeframeMap.All.Where(tfs.Contains).ToList();
        sortedTfs.AddRange(tfs.Except(TimeframeMap.All).OrderBy(x => x));
        Timeframes = string.Join(", ", sortedTfs);

        if (volCount == 0) VolumeStatus = "No";
        else if (volCount == count) VolumeStatus = "Yes";
        else VolumeStatus = $"Mixed ({volCount}/{count})";

        EarliestDate = minT > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(minT).ToString("yyyy-MM-dd") : "N/A";
        LatestDate = maxT > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(maxT).ToString("yyyy-MM-dd") : "N/A";
    }
}

public partial class ProvidersViewModel : ObservableObject
{
    private readonly CacheManager _cache;

    public ObservableCollection<ProviderStatRow> Rows { get; } = new();

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
        Rows.Clear();
        var dir = "data/cache";
        if (!Directory.Exists(dir))
        {
            StatusText = "No cache directory found.";
            return;
        }

        var files = Directory.GetFiles(dir, "*.json");
        var metas = new List<CacheMetadata>();

        foreach (var f in files)
        {
            var m = _cache.ReadCacheMetadata(f);
            if (m != null) metas.Add(m);
        }

        var grouped = metas.GroupBy(m => m.Source).OrderBy(g => g.Key);

        foreach (var g in grouped)
        {
            int count = g.Count();
            var tfs = g.Select(m => m.Timeframe).Distinct().ToList();
            int volCount = g.Count(m => m.HasVolume);
            
            long minT = g.Where(m => m.MinT > 0).Select(m => m.MinT).DefaultIfEmpty(0).Min();
            long maxT = g.Where(m => m.MaxT > 0).Select(m => m.MaxT).DefaultIfEmpty(0).Max();

            Rows.Add(new ProviderStatRow(g.Key, count, tfs, volCount, minT, maxT));
        }

        StatusText = $"Aggregated stats for {grouped.Count()} provider(s) across {files.Length} cached dataset(s).";
    }
}