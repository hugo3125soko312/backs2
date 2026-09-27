<!-- docs/provider_guide.md -->
# backs2 Provider Integration Guide

Six touchpoints. Every new `IDataProvider` must complete all six.

## 1. Provider class
Create `src/core/Providers/{Name}Provider.cs`. Implement `IDataProvider`:
- `string Name` — human-readable. Must match the keyed-transient string exactly.
- `Task<List<Kline>> FetchAsync(string symbol)` — delegate to the two-arg overload with `"1D"`.
- `Task<List<Kline>> FetchAsync(string symbol, string timeframe)` — the real work.
Rules:
- Return an empty `List<Kline>` on every failure. Never throw.
- Use `_httpClientFactory.CreateClient("ResilientClient")` — never `new HttpClient()`.
- Log with `LogTag.Provider`. Use `Debug` for request traces, `Info` for success, `Warn` for skipped frames, `Error` for exceptions (always pass `ex`).
- Resolve the symbol inside the provider. Never in `DataManager` or `IDataProvider`.

## 2. TimeframeMap
Add `To{Provider}(string tf) → string?` returning the wire format, or `null` for unsupported. Add a `case` for every canonical timeframe in `TimeframeMap.All`.

## 3. ConfigManager
Only if the provider needs an API key:
- Add a read-only property: `public string {Name}ApiKey { get; private set; } = "";`
- Add a `case` in the `.env` loader switch matching the variable name.

## 4. .env
Add the variable with a placeholder value. Never commit a real key. Add the same key to `.env.example`.

## 5. App.axaml.cs
Register as keyed transient:
```csharp
services.AddKeyedTransient<IDataProvider, {Name}Provider>("{Provider Display Name}");