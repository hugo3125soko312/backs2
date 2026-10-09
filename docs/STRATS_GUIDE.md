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

## 3a. Optimization Grid (min / max / step)

The Runner's **Optimize** command sweeps a parameter grid without any change to the source file. Numeric `// @param` declarations (`int` and `double` only) gain three runtime-only fields in the parameter panel:

- **Min** - lower bound of the sweep (inclusive).
- **Max** - upper bound of the sweep (inclusive).
- **Step** - increment between sampled values.

These are **not** declared in source. There is no `// @param` syntax for them - they live in the parameter panel and persist to settings, not to the `.cs` file.

### Derived values

- `ValueCount = floor((Max - Min) / Step) + 1` when all three fields parse, `Step > 0`, and `Max >= Min`. Any other state - blank field, unparseable text, `Step <= 0`, `Max < Min` - collapses the parameter to `ValueCount = 1`, meaning it contributes a single fixed value to the search space.
- `TotalCombinations` is the product of every parameter's `ValueCount`.
- `ExceedsCap` becomes true when `TotalCombinations > AppSettings.OptimizerMaxCombinations` (default **500**). Above the cap the Optimize command offers a random sample of the full grid rather than the Cartesian product.
- `HasGridRange` is true when `ValueCount > 1` - this is what distinguishes a swept axis from a fixed value in the UI.

### Persistence

Grid ranges are stored in `config/settings.json` under `AppSettings.OptimizerGridRanges`:

- Key: `"{StrategyName}|{ParamName}"`.
- Value: `"Min|Max|Step"` - the raw text from the three fields; empty fields stay empty.
- A cleared range serializes as `"||"`, which the Runner treats as a **delete** so empty entries do not accumulate.

Loading a strategy restores every persisted range for that strategy automatically. Presets (`Save Preset...` / `Presets...`) carry the grid ranges alongside parameter values, so a saved preset fully restores the sweep configuration.

### Author rules

- Only `int` and `double` parameters get the grid editor. `string` and `bool` are always fixed for the optimizer.
- A `string` parameter with `options: "A", "B"` is not swept - the optimizer uses whichever option is currently selected.
- Do not attempt to encode min/max/step in `// @param` comments. The parser reads only `Name`, `Type`, `Default`, and `options`.

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