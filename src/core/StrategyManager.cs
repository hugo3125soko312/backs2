using System.Collections.Generic; // Required for List<T>
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using backs2.Core.Metrics;    // Required for MetricRegistry
using backs2.Core.Models;     // Required for Kline

namespace backs2.Core;

public class StrategyManager
{
    private readonly MetricRegistry _registry;
    private readonly SemaphoreSlim _throttle = new SemaphoreSlim(1, 1);

    // Assuming ConfigManager is not used in the body, removed from constructor 
    // to match current usage, or keep it if you need it later.
    public StrategyManager(MetricRegistry registry) 
    {
        _registry = registry;
    }

    public async Task RunBacktestAsync(string script, List<Kline> data)
    {
        await _throttle.WaitAsync();
        try
        {
            var globals = new ScriptGlobals { Engine = _registry, Data = data };
            
            var options = ScriptOptions.Default
                .AddReferences(typeof(IMetric).Assembly)
                .AddImports("backs2.Core.Metrics", "backs2.Core.Models");

            await CSharpScript.EvaluateAsync(script, options, globals);
        }
        finally
        {
            _throttle.Release();
        }
    }
}

// Helper class for the script context
public class ScriptGlobals
{
    public MetricRegistry Engine { get; set; } = null!;
    public List<Kline> Data { get; set; } = null!;
}