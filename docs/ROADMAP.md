# Roadmap: Metrics & Caching Engine

## Phase 1: Persistence & Caching [COMPLETED]
- [x] Create `CacheManager`: Async JSON/Parquet serialization.
- [x] Implement MemoryCache layer in `DataManager`.
- [x] Integrate `IProgress<string>` for status feedback.

## Phase 2: Metrics Engine [COMPLETED]
- [x] Create `IMetric.cs` (Base/Normal distinction).
- [x] Implement `MetricRegistry`: Centralized metric repository.
- [x] Base Metrics (Session High/Low).

## Phase 3: Derived Metrics Implementation [COMPLETED]
- [x] Implement Normal Metrics (VWAP, Z-Score).
- [x] Create `MetricsViewModel` and `MetricsView`.
- [x] Expose metrics to `StrategyBuilder` via `Engine` global.

## Phase 4: Engine Hardening [COMPLETED]
- [x] Full Dependency Injection implementation.
- [x] Thread-safe backtesting with `SemaphoreSlim`.
- [x] Optimized 3-tier data access pattern.

## Future Roadmap: Engine Polish
- [ ] Implement advanced Parquet schema mapping.
- [ ] UI: Add real-time charting with `ScottPlot`.
- [ ] Security: Periodic dependency vulnerability auditing via `NuGetAudit`.