using System;

namespace backs2.Core.Overview;

public sealed class LastRunStore
{
    private readonly object _gate = new();
    private OverviewData? _current;

    public event EventHandler<OverviewData>? Changed;

    public OverviewData? Current
    {
        get { lock (_gate) { return _current; } }
    }

    public void Publish(OverviewData data)
    {
        if (data == null) return;

        lock (_gate) { _current = data; }
        Changed?.Invoke(this, data);
    }
}