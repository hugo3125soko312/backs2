using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using backs2.Core.Metrics;
using backs2.Core.Models;

namespace backs2.Core;

public sealed class StrategyRunResult
{
    public string Path { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
}

public class StrategyManager
{
    private readonly MetricRegistry _registry;
    private readonly LogManager _log;
    private readonly SemaphoreSlim _throttle = new(1, 1);

    private readonly ConcurrentDictionary<string, CachedScript> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedScript(DateTime LastWriteUtc, long Size, ScriptRunner<object?> Runner);

    public StrategyManager(MetricRegistry registry, LogManager log)
    {
        _registry = registry;
        _log = log;
    }

    public async Task<StrategyRunResult> RunBacktestFromFileAsync(
        string path,
        List<Kline> data,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
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

            var globals = new ScriptGlobals
            {
                Engine = _registry,
                Data = data,
                Log = _log.Info
            };

            await runner(globals).ConfigureAwait(false);

            _log.Info($"Strategy executed: {System.IO.Path.GetFileName(path)}");

            return new StrategyRunResult
            {
                Path = path,
                Success = true,
                Duration = DateTime.UtcNow - started
            };
        }
        catch (Exception ex)
        {
            _log.Error($"Strategy failed: {System.IO.Path.GetFileName(path)} - {ex.Message}");
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
            _throttle.Release();
        }
    }

    public async Task RunBacktestAsync(string script, List<Kline> data)
    {
        await _throttle.WaitAsync().ConfigureAwait(false);
        try
        {
            var globals = new ScriptGlobals
            {
                Engine = _registry,
                Data = data,
                Log = _log.Info
            };

            var options = ScriptOptions.Default
                .AddReferences(typeof(IMetric).Assembly)
                .AddImports("backs2.Core.Metrics", "backs2.Core.Models");

            await CSharpScript.EvaluateAsync(script, options, globals).ConfigureAwait(false);
        }
        finally
        {
            _throttle.Release();
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
            return existing.Runner;
        }

        var options = ScriptOptions.Default
            .AddReferences(typeof(IMetric).Assembly)
            .AddImports("backs2.Core.Metrics", "backs2.Core.Models");

        var script = CSharpScript.Create<object?>(
            source,
            options,
            typeof(ScriptGlobals));

        var runner = script.CreateDelegate();

        _cache[path] = new CachedScript(lastWriteUtc, size, runner);
        return runner;
    }
}

public class ScriptGlobals
{
    public MetricRegistry Engine { get; set; } = null!;
    public List<Kline> Data { get; set; } = null!;
    public Action<string> Log { get; set; } = _ => { };
}