using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace backs2.Core.GuiLoading;

/// <summary>
/// Streams a producer into a sink in bounded batches, yielding to the
/// Avalonia dispatcher between flushes so the UI stays interactive.
/// Lifted from the LogsViewModel pattern (Phase 22E) into a reusable
/// core module (ROADMAP Phase 38A).
/// </summary>
public sealed class StreamingLoader<T> : IDisposable
{
    private readonly Action<IReadOnlyList<T>> _sink;
    private readonly int _batchSize;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public StreamingLoader(Action<IReadOnlyList<T>> sink, int batchSize = 100)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "batchSize must be >= 1");
        _batchSize = batchSize;
    }

    public Task StartAsync(IEnumerable<T> producer, CancellationToken ct = default)
    {
        if (producer == null) throw new ArgumentNullException(nameof(producer));
        if (_disposed) throw new ObjectDisposedException(nameof(StreamingLoader<T>));

        Cancel();

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = linked;
        var token = linked.Token;

        return Task.Run(() => RunAsync(producer, token), token);
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private async Task RunAsync(IEnumerable<T> producer, CancellationToken token)
    {
        var batch = new List<T>(_batchSize);
        try
        {
            foreach (var item in producer)
            {
                if (token.IsCancellationRequested) return;

                batch.Add(item);
                if (batch.Count < _batchSize) continue;

                var toApply = batch;
                batch = new List<T>(_batchSize);
                await Dispatcher.UIThread.InvokeAsync(() => _sink(toApply));
            }

            if (batch.Count > 0 && !token.IsCancellationRequested)
            {
                var toApply = batch;
                await Dispatcher.UIThread.InvokeAsync(() => _sink(toApply));
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
    }
}
