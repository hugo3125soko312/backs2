# Roadmap: Metrics, Caching & Strategy Catalog

## Phase 1-4: Core Foundation [COMPLETED]
- [x] Persistence & Caching, Metrics Engine, Derived Metrics, Engine Hardening.

## Phase 5: Provider Scaling & Resilience [COMPLETED]
- [x] Integrate `Microsoft.Extensions.Http.Resilience` for auto-retry logic.
- [x] Implement `MassiveProvider` and `BinanceProvider` using keyed services.
- [x] Centralize resilience settings in `ConfigManager`.

## Phase 6: Disk-Streamed Strategy Catalog [COMPLETED]
- [x] Remove the in-app strategy builder (`StratsBuilder/`).
- [x] Add `StrategyCatalogService` (FileSystemWatcher + lazy loading).
- [x] `StrategyManager`: file-based execution, per-file compile cache, `LogManager` wiring.
- [x] Rewrite the Strategies tab: list catalog, preview source, run selected.
- [x] Exclude `data/**/*.cs` from compilation in `backs2.csproj`.
- [x] Logs tab: live refresh + selectable output.
- [x] Fix `LogManager.GetLatestLogs()` (comment swallowed the method) and add a missing-file guard.

## Phase 7: Cleanup & Hardening [PLANNED]
- [ ] Wire `ChartView` / `DataView` code-behinds to their ViewModels via DI (currently unset).
- [ ] Reduce build warnings to zero.
- [ ] Decide on `data/` git policy (currently untracked).