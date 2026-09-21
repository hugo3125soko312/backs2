# Roadmap: backs2 Trading Engine

## Phase 1-4: Core Foundation [COMPLETED]
- [x] Persistence & Caching tier (`CacheManager`, `IMemoryCache`).
- [x] Metrics Engine (`IMetric`, `MetricRegistry`, base + derived metrics).
- [x] Derived Metrics (VWAP, Z-Score, SessionHigh).
- [x] Engine Hardening (DI-based xManager pattern, no statics).

## Phase 5: Provider Scaling & Resilience [COMPLETED]
- [x] Integrate `Microsoft.Extensions.Http.Resilience` for auto-retry logic.
- [x] Implement `MassiveProvider` and `YahooProvider` using keyed services.
- [x] Centralize resilience settings in `ConfigManager`.

## Phase 6: Disk-Streamed Strategy Catalog [COMPLETED]
- [x] Remove the in-app strategy builder (`StratsBuilder/`).
- [x] Add `StrategyCatalogService` (FileSystemWatcher + lazy loading).
- [x] `StrategyManager`: file-based execution, per-file compile cache, `LogManager` wiring.
- [x] Rewrite the Strategies tab: list catalog, preview source, run selected.
- [x] Exclude `data/**/*.cs` from compilation in `backs2.csproj`.
- [x] Logs tab: live refresh + selectable output.
- [x] Fix `LogManager.GetLatestLogs()` (comment swallowed the method) and add a missing-file guard.

## Phase 7: Provider Ordering & Secrets [COMPLETED]
- [x] Replace `AppSettings.ActiveProviderKey` with ordered `List<string> ProviderOrder`.
- [x] `DataManager`: walk `ProviderOrder`, first non-empty result wins, log each attempt.
- [x] Move API keys out of `config/settings.json` into `.env` (`MASSIVE_PROVIDER_API_KEY`, `YAHOO_PROVIDER_API_KEY`).
- [x] `ConfigManager`: load `.env` at startup, expose keys as read-only properties.
- [x] Delete `YahooAuthService.cs`; consolidate Yahoo into a single provider file.
- [x] Complete `MassiveProvider` against `/v2/aggs/ticker/{ticker}/range/...`.
- [x] Per-provider symbol auto-resolution:
  - Massive: `XAUUSD → C:XAUUSD`, `EURUSD → C:EURUSD`, stocks pass through.
  - Yahoo: `XAUUSD → XAUUSD=X`, `EURUSD → EURUSD=X`, stocks pass through.
- [x] Handle Massive `status: "DELAYED"` as a success (results still present).
- [x] Config tab: provider order editor (add / remove / move up / move down).
- [x] Verify end-to-end: `XAUUSD` → 312 bars → `HelloVwap` strategy executes.

## Phase 8: Strategy UX & Data Selection [PLANNED]
- [ ] Strategies tab: replace hard-coded `XAUUSD` in `StratsViewModel.RunSelectedAsync` with a user-selectable ticker (dropdown or text input bound to a new `[ObservableProperty] string _selectedSymbol`).
- [ ] Add a symbol picker sourced from `DataManager.GetAvailableData()` plus a free-text entry.
- [ ] Display run results (Sharpe, WinRate) in the Strategies tab — the properties already exist but are never populated.

## Phase 9: Logs & Observability [PLANNED]
- [ ] Logs tab: switch from 2 s polling to a live tail driven by `FileSystemWatcher` on the log file.
- [ ] Logs tab: initial population on first open (currently only fills after the first timer tick).
- [ ] Investigate log file rotation: every session only ever shows one `runtime*.log` file. Verify Serilog's `RollingInterval.Day` is actually producing daily files; check `logs/` after several days and confirm old files persist.
- [ ] Add a "copy all" / "clear view" button to the Logs tab.

## Phase 10: View & Shell Wiring [PLANNED]
- [ ] Wire `ChartView.axaml.cs` to its ViewModel via DI (currently unset, no `DataContext`).
- [ ] Wire `DataView.axaml.cs` to its ViewModel via DI (currently unset).
- [ ] Confirm every tab's `.axaml.cs` follows the `if (Application.Current is App app) DataContext = app.Services.GetRequiredService<TViewModel>();` pattern.
- [ ] Ensure `ChartView` / `DataView` have ViewModels registered in `App.axaml.cs`.

## Phase 11: Hardening & Housekeeping [PLANNED]
- [ ] Reduce build warnings to zero (`dotnet build 2>&1 | Select-String warning`).
- [ ] Decide on `data/` git policy — currently untracked, contains cache + strategies + downloads.
- [ ] Audit `YahooProvider` for rate limiting (recent log shows a 429 for `BTCUSD`). Consider a per-provider throttle or backoff policy.
- [ ] Add a unit test project covering: symbol resolution per provider, `DataManager` fallback ordering, `StrategyCatalogService` scan/changed events.
- [ ] `docs/`: add a `docs/STRATEGIES.md` authoring guide for writing strategies against `ScriptGlobals`.