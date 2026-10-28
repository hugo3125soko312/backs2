using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using backs2.Core.Data;
using backs2.Core.Providers;

namespace backs2.Core;

public sealed record QueuedDataset(
    string Symbol,
    string Timeframe,
    string Provider
)
{
    public string Key => $"{Symbol}@{Timeframe}";
}

public sealed class SelectionQueue
{
    private readonly ConfigManager _cfg;
    private readonly object _gate = new();
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DataOrigin> _origins = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<QueuedDataset> Items { get; } = new();

    public event EventHandler? Changed;

    public SelectionQueue(ConfigManager cfg)
    {
        _cfg = cfg;
        LoadFromSettings();
    }

    public bool Contains(string symbol, string timeframe)
    {
        lock (_gate)
        {
            return _keys.Contains(KeyOf(symbol, timeframe));
        }
    }

    public void Add(string symbol, string timeframe, string provider)
        => Add(symbol, timeframe, provider, DataOrigin.Legacy);

    public void Add(string symbol, string timeframe, string provider, DataOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(timeframe))
            return;

        lock (_gate)
        {
            if (Contains(symbol, timeframe)) return;

            Items.Add(new QueuedDataset(symbol, timeframe, provider ?? "Unknown"));
            _keys.Add(KeyOf(symbol, timeframe));
            _origins[KeyOf(symbol, timeframe)] = origin;
        }

        Persist();
        LogManager.Info(LogTag.Data, $"queued {symbol}@{timeframe}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string symbol, string timeframe)
    {
        QueuedDataset? match;
        lock (_gate)
        {
            match = Items.FirstOrDefault(i =>
                string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.Timeframe, timeframe, StringComparison.OrdinalIgnoreCase));

            if (match == null) return;
            Items.Remove(match);
            _keys.Remove(KeyOf(symbol, timeframe));
            _origins.Remove(KeyOf(symbol, timeframe));
        }

        Persist();
        LogManager.Info(LogTag.Data, $"dequeued {symbol}@{timeframe}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (Items.Count == 0) return;
            Items.Clear();
            _keys.Clear();
            _origins.Clear();
        }

        Persist();
        LogManager.Info(LogTag.Data, "queue cleared");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<QueuedDataset> Snapshot()
    {
        lock (_gate) { return Items.ToList(); }
    }

    private void LoadFromSettings()
    {
        var raw = _cfg.Settings.QueuedDatasets ?? new List<string>();
        int droppedBroken = 0;
        foreach (var entry in raw)
        {
            if (!IsValidTimeframeEntry(entry)) { droppedBroken++; continue; }
        {
            var at = entry.IndexOf('@');
            if (at <= 0 || at >= entry.Length - 1) continue;
            var symbol = entry.Substring(0, at);
            var timeframe = entry.Substring(at + 1);
            Items.Add(new QueuedDataset(symbol, timeframe, "Unknown"));
            _keys.Add(KeyOf(symbol, timeframe));
            var stored = _cfg.Settings.QueuedDatasetOrigins.TryGetValue(KeyOf(symbol, timeframe), out var s) && Enum.TryParse<DataOrigin>(s, out var parsed) ? parsed : DataOrigin.Legacy;
            _origins[KeyOf(symbol, timeframe)] = stored;
        }

        }

        if (droppedBroken > 0)
            LogManager.Warn(LogTag.Data, $"dropped {droppedBroken} queued dataset(s) with invalid timeframe from settings");
        if (Items.Count > 0)
            LogManager.Info(LogTag.Data, $"restored {Items.Count} queued dataset(s) from settings");
    }

    public DataOrigin GetOrigin(string symbol, string timeframe)
    {
        lock (_gate)
        {
            return _origins.TryGetValue(KeyOf(symbol, timeframe), out var o) ? o : DataOrigin.Legacy;
        }
    }

    private static bool IsValidTimeframeEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        var at = entry.IndexOf('@');
        if (at <= 0 || at >= entry.Length - 1) return false;
        var tf = entry.Substring(at + 1);
        foreach (var tfEntry in TimeframeMap.All) if (string.Equals(tf, tfEntry, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(tf, "tick", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string KeyOf(string symbol, string timeframe) => symbol + "@" + timeframe;

    private void Persist()
    {
        lock (_gate)
        {
            _cfg.Settings.QueuedDatasets = Items.Select(i => i.Key).ToList();
            _cfg.Settings.QueuedDatasetOrigins = _origins.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        }
        _cfg.SaveConfig();
    }
}