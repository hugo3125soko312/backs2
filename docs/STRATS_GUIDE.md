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

### Sweep axes

A parameter contributes more than one value to the search space in exactly three cases, checked in this order by `ParameterGridBuilder.GetValuesFor`:

1. **`bool` — always swept to `{true, false}`.** No user input, no override. A strategy with two bool params and no numeric axes produces a 4-combination grid whether the author intended it or not.
2. **Numeric (`int` / `double`) with `Min`, `Max`, and `Increment > 0` all set** — `ValueCount = floor((Max - Min) / Step) + 1`. Capped at 10,000 values per parameter (`ParameterGridBuilder.PerParameterValueGuard`). Any other numeric state (missing field, unparseable text, `Step <= 0`, `Max < Min`) collapses to the fixed `EffectiveValue`.
3. **`string` with a non-empty `options:` list** — swept over the declared options in declaration order. The `options:` list is what makes the axis; the same string without it is fixed.

Anything else — a `string` without `options`, a numeric with an empty Min/Max/Step — contributes exactly one fixed value.

### GUI editor vs. sweep axis

The min/max/step fields render **only** for numeric params (`RunnerParameterViewModel.IsNumeric`). `bool` and `string-with-options` axes never show the three fields — their sweep comes from the type and the declaration, not from user-entered bounds. This is deliberate, not an omission: there is nothing to configure for them.

### Known discrepancy (bug)

`RunnerViewModel.TotalCombinations` is computed from `RunnerParameterViewModel.ValueCount`, which returns `1` for every non-numeric parameter. It does **not** count `bool` or `string-with-options` axes. `ParameterGridBuilder` **does** count them. Consequence: for a strategy whose only axis is a `string` with `options:` (e.g. `EtWindow`'s `Side` param), the Runner's combination summary shows `1` while the optimizer actually produces `N` combinations (one per option). The batch written to disk is correct — only the UI counter is wrong.

### Persistence

Grid ranges are stored in `config/settings.json` under `AppSettings.OptimizerGridRanges`:

- Key: `"{StrategyName}|{ParamName}"`.
- Value: `"Min|Max|Step"` — raw text from the three numeric fields; empty fields stay empty.
- `"||"` means delete — the Runner removes the entry rather than accumulating empties.

Only numeric axes persist here. `bool` and `string-with-options` sweeps are re-derived from the strategy source on every load; there is nothing to store.

Presets (`Save Preset...` / `Presets...`) carry the ranges alongside parameter values, so a saved preset fully restores the sweep configuration.

### Author rules

- The `// @param` parser reads only `Name`, `Type`, `Default`, and `options`. Do not attempt to encode min/max/step in source.
- To sweep a numeric parameter, set its min/max/step in the parameter panel; the values persist to `config/settings.json`.
- To sweep a string parameter, declare it with `options:`. There is no other mechanism.
- Every `bool` parameter is a two-value sweep axis by definition — if a bool must be pinned to one value, the only way is to remove the parameter from the strategy.

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