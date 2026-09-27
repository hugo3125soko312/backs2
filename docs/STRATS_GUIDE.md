<!-- docs\STRATS_GUIDE.md -->
# backs2 Strategy Authoring Guide

## 1. Engine Philosophy
- Strategies in backs2 are **signal generators**, not account managers. 
- **Do not** enforce prop-firm rules (Max Daily Drawdown, profit consistency limits, trailing drawdowns). The `MonteCarloEngine` natively evaluates constraints against the time-domain using `TimeSlice[]`.
- **Do not** implement percentage-based compounding or dynamic position sizing unless specifically testing a multi-lot scale out. Standard position size defaults to `1.0`.

## 2. Script Shape & Globals
Strategies are Roslyn-compiled top-level statements. They execute against `RunnerScriptGlobals`, providing access to:
- `Data` (`List<Kline>`): The price series. Properties: `Timestamp`, `Open`, `High`, `Low`, `Close`, `Volume`.
- `Engine` (`MetricRegistry`): Access metrics via `Engine.GetMetric("Name").Calculate(Data)`. Never throws; returns `NullMetric` on misses.
- `Trades` (`TradeRecorder`): Use `Advance(i)` to sync the time cursor, then `Enter(side, price)` and `Exit(price)`.
- `Params` (`Dictionary<string, string>`): Read user-configured values.
- `Log` (`Action<string>`): Print diagnostic info to the engine logs.

## 3. Parameter Declaration
Define parameters at the very top of the script using `// @param` comments. The UI parses these to auto-generate the parameter panel.
- Supported types: `int`, `double`, `bool`, `string`.
- Format: `// @param <Name> <Type> = <Default> [options: "A", "B"]`
- **Crucial C# Rule**: Always use `System.Globalization.CultureInfo.InvariantCulture` when parsing `double` to prevent decimal-comma locale crashes.

## 4. Standard Loop Structure
```csharp
using System;
using System.Globalization;
using backs2.Core.Runner;

// @param Length int = 14
// @param Target double = 2.0
// @param Side string = "Long" options: "Long", "Short"

var culture = CultureInfo.InvariantCulture;
int length = int.TryParse(Params["Length"], out var l) ? l : 14;
double target = double.TryParse(Params["Target"], NumberStyles.Any, culture, out var t) ? t : 2.0;
string side = Params["Side"];

bool inTrade = false;
double tpPrice = 0.0;

for (int i = 0; i < Data.Count; i++)
{
    // 1. Always advance the cursor first
    Trades.Advance(i);
    var kline = Data[i];

    // 2. Intra-candle exit evaluation
    if (inTrade)
    {
        if (side == "Long" && kline.High >= tpPrice)
        {
            Trades.Exit(tpPrice);
            inTrade = false;
        }
        continue;
    }

    // 3. Lookback guards
    if (i < length) continue;

    // 4. Entry evaluation
    if (kline.Close > Data[i - 1].High) // Example condition
    {
        tpPrice = kline.Close + target;
        Trades.Enter(side, kline.Close);
        inTrade = true;
    }
}