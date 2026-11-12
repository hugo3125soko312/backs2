# Pending Roadmap Items

## Immediate Next Steps

### 1. Pending User Testing [BLOCKING] ⚠️

Thirteen commits landed between 4327e85 and 45e0eb2 without runtime testing. Every one built clean but none has been exercised.

| # | Feature | Commit | Test |
|---|---------|--------|------|
| 1 | Log O(n²) and flush coalescing | 45e0eb2 | Open Logs tab, run Optimize, watch live |
| 2 | Cache metadata memoization | 45e0eb2 | [PASSED 2026-10-28] |
| 3 | Data tab filter debounce | 45e0eb2 | [PASSED 2026-10-28] |
| 4 | GPU batch threshold | 45e0eb2 | [PASSED 2026-10-28] |
| 5 | Optimizer progress throttle | 7f661e4 | [PASSED 2026-10-28] |
| 6 | Backtest dispatch through IComputeDispatcher | 709fe31 | [PASSED 2026-10-28] |
| 7 | Data tab refresh instrumentation | 7f661e4 | [PASSED 2026-10-28] |
| 8 | In-group column sorting | fa63e26 | [PASSED 2026-10-28] |
| 9 | Avalonia 12 upgrade | eb1c666 | [PASSED 2026-11-11] |

### 2. Phase 10.11C2: Equity Curve Rendering [PENDING]

- Extend Monte Carlo result to store per-simulation equity curves
- Spaghetti plot on the Monte Carlo tab

### 3. Phase 10.7: Chart Tab [IN PROGRESS]

#### 10.7C -- Timeframe Switching [PENDING]
#### 10.7D -- Crosshair & Readout [PENDING]
#### 10.7E -- Polish [IN PROGRESS]

### 4. Phase 11: Data Automation [IN PROGRESS]

#### 11F -- Automatic start-position detection [PENDING]
- Capture full-window frame after focus + maximize
- Locate price/time axis and chart plot area
- Derive click point automatically
- Drop 200× right-arrow prelude
- Keep "Calibrate Click" as optional override
- Log detected start position

### 5. Phase 18: Data Tab Enhancements [IN PROGRESS]

#### 18B -- Data tab first-click latency [PENDING - Baseline needed]
- Instrument DataViewModel ctor body with Stopwatch markers
- Profile GetAvailableData (EnumerateFiles vs ReadCacheMetadata)
- Candidate A: Move RefreshData() out of ctor to async kick-off
- Candidate B: Parallelise per-file metadata reads
- Candidate C: In-memory index (likely overkill)
- Post-fetch refresh measurement
- Target: interactive within 100ms, grid populated within 500ms

### 6. Phase 24: Tab Responsiveness [IN PROGRESS]

#### 24C -- Config tab [CLOSED]
#### 24D -- Metrics tab [PENDING]
#### 24E -- Overview tab [PENDING]
#### 24F -- Logs tab [PENDING]
#### 24G -- Chart tab [PENDING]
#### 24H -- Runner tab [PENDING]
#### 24I -- Monte Carlo tab [PENDING]

---

## Completed Phases (Recent)

| Phase | Status | Date |
|-------|--------|------|
| 10.11C3 | Monte Carlo Toggle Performance | [COMPLETED 2026-10-28] |
| 24B | Accounts tab responsiveness | [COMPLETED 2026-10-28] |
| 18A | Total Range column | [COMPLETED] |
| 17 | Startup Path Optimization | [COMPLETED] |
| Phase 36 | Runtime Performance Monitor | [COMPLETED] |
| 9 | Avalonia 12 upgrade | [COMPLETED 2026-11-11] |

---

## Deferred to v1.1

From walkforward design:
- Stitched equity chart on results subtab
- Parameter stability small-multiples visualization
- Multi-dataset walkforward in one run
- OOS indicator warm-up optimization

---

## v1.1 Enhancements

- Phase 26: Parameter Optimizer & Version Store [PLANNED]
- Phase 35: exec-bridge Production Hardening [PLANNED]
  - 35A -- Sandboxed Worker Boundary
  - 35B -- Typed Command Registry
  - 35C -- Job Model & Persistence
  - 35D -- Approval UI
  - 35E -- Structured Risk Classification
  - 35F -- Streaming Output & Cancellation
  - 35G -- Observability
  - 35H -- Auth & Remote Hardening [DEFERRED]
