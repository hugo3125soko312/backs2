using System;
using System.Collections.Generic;
using System.Linq;

namespace backs2.Core.Overview;

public sealed class LastRunStore
{
    private readonly object _gate = new();
    private IReadOnlyList<OverviewData> _batch = Array.Empty<OverviewData>();

    public event EventHandler<IReadOnlyList<OverviewData>>? Changed;

    public IReadOnlyList<OverviewData> Batch
    {
        get { lock (_gate) { return _batch; } }
    }

    public OverviewData? Current
    {
        get
        {
            lock (_gate)
            {
                return _batch.Count > 0 ? _batch[_batch.Count - 1] : null;
            }
        }
    }

    public void Publish(OverviewData data)
    {
        if (data == null) return;
        PublishBatch(new[] { data });
    }

    public void PublishBatch(IReadOnlyList<OverviewData> batch)
    {
        if (batch == null || batch.Count == 0) return;

        lock (_gate) { _batch = batch.ToList(); }
        Changed?.Invoke(this, _batch);
    }
}