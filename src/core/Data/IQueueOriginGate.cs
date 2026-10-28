using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;
using backs2.Core.Data;

namespace backs2.Core.Data;

public enum QueueGateDecision { Allow, Warn, Block }

public sealed record QueueGateResult(
    QueueGateDecision Decision,
    IReadOnlyList<QueuedDataset> RawTickEntries,
    string Message);

public interface IQueueOriginGate
{
    Task<QueueGateResult> CheckAsync(IReadOnlyList<QueuedDataset> entries, CancellationToken ct = default);
}

public sealed class QueueOriginGate : IQueueOriginGate
{
    private readonly CacheManager _cache;

    public QueueOriginGate(CacheManager cache) { _cache = cache; }

    public Task<QueueGateResult> CheckAsync(IReadOnlyList<QueuedDataset> entries, CancellationToken ct = default)
    {
        if (entries == null || entries.Count == 0)
            return Task.FromResult(new QueueGateResult(QueueGateDecision.Allow, Array.Empty<QueuedDataset>(), ""));

        var rawTicks = new List<QueuedDataset>();
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = "data/cache/" + e.Key.Replace('@', '_') + ".json";
            if (!System.IO.File.Exists(path)) continue;
            var meta = _cache.ReadCacheMetadata(path);
            if (meta == null) continue;
            if (meta.Origin == DataOrigin.RawTick) rawTicks.Add(e);
        }

        if (rawTicks.Count == 0)
            return Task.FromResult(new QueueGateResult(QueueGateDecision.Allow, Array.Empty<QueuedDataset>(), ""));

        var names = string.Join(", ", rawTicks.Select(r => r.Key));
        var msg = "Queue contains raw-tick compiled data: " + names +
                  ". Compile-on-demand is not available yet; compiled bars are used as-is.";
        return Task.FromResult(new QueueGateResult(QueueGateDecision.Warn, rawTicks, msg));
    }
}