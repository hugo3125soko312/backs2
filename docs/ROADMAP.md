# Roadmap: Metrics & Caching Engine

## Phase 1-4: Core Foundation [COMPLETED]
- [x] Persistence & Caching, Metrics Engine, Derived Metrics, Engine Hardening.

## Phase 5: Provider Scaling & Resilience [COMPLETED]
- [x] Integrate `Microsoft.Extensions.Http.Resilience` for auto-retry logic.
- [x] Implement `MassiveProvider` and `BinanceProvider` using keyed services.
- [x] Centralize resilience settings in `ConfigManager`.

## Future Roadmap: Engine Polish
- [ ] Implement advanced Parquet schema mapping.
- [ ] UI: Add real-time charting with `ScottPlot`.
- [ ] Security: Periodic dependency vulnerability auditing via `NuGetAudit`.