using System;
using System.Collections.Generic;

namespace backs2.Core.GuiLoading;

/// <summary>
/// Creates a <see cref="StreamingLoader{T}"/> bound to a specific sink.
/// Registered as a singleton in DI (ROADMAP Phase 38A). A generic loader
/// cannot itself be a singleton (the type parameter varies per caller), so
/// the factory supplies the registration while the loader instance stays
/// per-use.
/// </summary>
public interface IStreamingLoaderFactory
{
    StreamingLoader<T> Create<T>(Action<IReadOnlyList<T>> sink, int batchSize = 100);
}

public sealed class StreamingLoaderFactory : IStreamingLoaderFactory
{
    public StreamingLoader<T> Create<T>(Action<IReadOnlyList<T>> sink, int batchSize = 100)
        => new StreamingLoader<T>(sink, batchSize);
}
