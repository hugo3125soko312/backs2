using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.Data;

public sealed record TickCompileProgress(double Percent, string Message);

public sealed record TickCompileResult(
    string Symbol,
    string Timeframe,
    int LinesRead,
    int Bars,
    IReadOnlyList<long> ZeroBidTicks,
    string CacheKey);

public interface ITickCompiler
{
    /// <summary>
    /// Compile a raw-tick CSV into kline bars for the given timeframe and
    /// persist them with DataOrigin.RawTick / source="MT5 Parsed".
    /// Symbol is parsed from the CSV filename (before the first underscore).
    /// </summary>
    Task<TickCompileResult> CompileAsync(
        string csvPath,
        string timeframe,
        IProgress<TickCompileProgress>? progress = null,
        CancellationToken ct = default);
}