using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using backs2.Core.Metrics;
using backs2.Core.Models;
using backs2.Core.Runner;

namespace backs2.Core;

public sealed class StrategyRunResult
{
    public string Path { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<Trade> Trades { get; init; } = Array.Empty<Trade>();
}

public class StrategyManager
{
    private readonly MetricRegistry _registry;
    private readonly SemaphoreSlim _throttle = new(1, 1);

    private readonly ConcurrentDictionary<string, CachedScript> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedScript(DateTime LastWriteUtc, long Size, ScriptRunner<object?> Runner);

    /// <summary>
    /// Serialises backtests through a single-thread throttle. Enabled by
    /// default for the single-run UI path. The parameter optimizer sets this
    /// to false after the --verify-strategy-parallelism check confirms the
    /// compiled <see cref="ScriptRunner{T}"/> is safe for concurrent
    /// invocation (see docs/ROADMAP_ARCHIVE.md Phase 26B.1).
    /// </summary>
    public bool SerializeBacktests { get; set; } = true;

    public StrategyManager(MetricRegistry registry)
    {
        _registry = registry;
    }

    public async Task<StrategyRunResult> RunBacktestFromFileAsync(
        string path,
        List<Kline> data,
        Dictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;

        bool tookLock = false;
        if (SerializeBacktests)
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            tookLock = true;
        }

        try
        {
            if (!System.IO.File.Exists(path))
            {
                return new StrategyRunResult
                {
                    Path = path,
                    Success = false,
                    Error = $"Strategy file not found: {path}",
                    Duration = DateTime.UtcNow - started
                };
            }

            var info = new System.IO.FileInfo(path);
            var source = await System.IO.File.ReadAllTextAsync(path, ct).ConfigureAwait(false);

            var runner = GetOrCreateRunner(path, info.LastWriteTimeUtc, info.Length, source);

            var recorder = new TradeRecorder(data);

            var globals = new RunnerScriptGlobals
            {
                Engine = _registry,
                Data = data,
                Log = msg => LogManager.Info(LogTag.Strategy, msg),
                Trades = recorder,
                Params = parameters ?? new Dictionary<string, string>()
            };

            await runner(globals).ConfigureAwait(false);

            LogManager.Info(LogTag.Strategy, $"executed {System.IO.Path.GetFileName(path)}");

            return new StrategyRunResult
            {
                Path = path,
                Success = true,
                Duration = DateTime.UtcNow - started,
                Trades = recorder.Closed
            };
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Strategy, $"failed {System.IO.Path.GetFileName(path)}", ex);
            return new StrategyRunResult
            {
                Path = path,
                Success = false,
                Error = ex.Message,
                Duration = DateTime.UtcNow - started
            };
        }
        finally
        {
            if (tookLock) _throttle.Release();
        }
    }

    public async Task RunBacktestAsync(string script, List<Kline> data)
    {
        bool tookLock = false;
        if (SerializeBacktests)
        {
            await _throttle.WaitAsync().ConfigureAwait(false);
            tookLock = true;
        }

        try
        {
            var recorder = new TradeRecorder(data);

            var globals = new RunnerScriptGlobals
            {
                Engine = _registry,
                Data = data,
                Log = msg => LogManager.Info(LogTag.Strategy, msg),
                Trades = recorder,
                Params = new Dictionary<string, string>()
            };

            var options = ScriptOptions.Default
                .AddReferences(typeof(IMetric).Assembly)
                .AddImports("backs2.Core.Metrics", "backs2.Core.Models", "backs2.Core.Runner");

            await CSharpScript.EvaluateAsync(script, options, globals).ConfigureAwait(false);
        }
        finally
        {
            if (tookLock) _throttle.Release();
        }
    }

    private ScriptRunner<object?> GetOrCreateRunner(
        string path,
        DateTime lastWriteUtc,
        long size,
        string source)
    {
        if (_cache.TryGetValue(path, out var existing)
            && existing.LastWriteUtc == lastWriteUtc
            && existing.Size == size)
        {
            LogManager.Debug(LogTag.Strategy, $"compile cache hit: {System.IO.Path.GetFileName(path)}");
            return existing.Runner;
        }

        LogManager.Debug(LogTag.Strategy, $"compile: {System.IO.Path.GetFileName(path)}");

        var options = ScriptOptions.Default
            .AddReferences(typeof(IMetric).Assembly)
            .AddImports("backs2.Core.Metrics", "backs2.Core.Models", "backs2.Core.Runner");

        var script = CSharpScript.Create<object?>(
            source,
            options,
            typeof(RunnerScriptGlobals));

        var runner = script.CreateDelegate();

        _cache[path] = new CachedScript(lastWriteUtc, size, runner);
        return runner;
    }
}