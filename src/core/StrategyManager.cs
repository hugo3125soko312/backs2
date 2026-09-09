using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using backs2.Core;

namespace backs2.Core;

public record BacktestResult(double SharpeRatio, double Drawdown, double WinRate);

public class StrategyManager
{
    private readonly ConfigManager _config;
    private readonly SemaphoreSlim _throttle;

    public StrategyManager(ConfigManager config)
    {
        _config = config;
        _throttle = new SemaphoreSlim(_config.Settings.MaxThreads);
    }

    public async Task<bool> CompileStrategy(string code)
    {
        try { await CSharpScript.EvaluateAsync(code); return true; }
        catch { return false; }
    }

    public async Task RunBacktestAsync(string stratId, string dataSymbol)
    {
        await _throttle.WaitAsync();
        try { await Task.Delay(100); }
        finally { _throttle.Release(); }
    }

    public BacktestResult CalculateStatistics(List<double> returns)
    {
        if (returns.Count == 0) return new BacktestResult(0, 0, 0);
        return new BacktestResult(returns.Average() / 0.05, returns.Min(), returns.Count(r => r > 0) / (double)returns.Count);
    }
}