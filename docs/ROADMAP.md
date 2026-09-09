# Roadmap: Metrics & Caching Engine
## Phase 1: Persistence & Caching
- [ ] Create `CacheManager`: Handles serialization of raw data to `data/cache/` (Parquet/JSON).
- [ ] Implement `CacheWrapper` in `DataManager` to check disk before calling API.

## Phase 2: Metrics Engine
- [ ] Create `src/core/Metrics/IMetric.cs` (Base/Normal distinction).
- [ ] Implement `MetricRegistry`: A centralized service for the Strategy Builder to query.
- [ ] Implement Base Metrics: OHLCV, Delta, Session High/Low/Mid.

## Phase 3: Derived Metrics Implementation
- [ ] Implement Normal Metrics (VWAP, Z-Score, Gamma, etc.).
- [ ] Add `MetricsTab` to UI.
- [ ] Expose Metrics to `StrategyBuilder` via `MetricRegistry`.