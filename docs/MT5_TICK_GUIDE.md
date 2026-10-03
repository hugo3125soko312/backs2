<!-- docs\MT5_TICK_GUIDE.md -->
# MT5 Raw Tick Data Guide

## Overview

The **MT5 Ticks** tab exposes four subtabs:

- **Extraction** — fetch raw `MqlTick` records from a running MetaTrader 5 terminal over a local TCP bridge, stream them to CSV, and cross-check a fetched range against the terminal's own tick count.
- **Requester** — force the terminal to download a tick range from the broker into the local `.tkc` cache. Uses the same bridge; no raw ticks cross the wire.
- **.tkc Extract** — read the terminal's local monthly `.tkc` tick-cache files directly from disk and aggregate them into kline cache.
- **Parsing** — convert a saved MT5 tick CSV into OHLCV bars and cache them so every Kline-consuming tab (Data, Chart, Runner, Monte Carlo) can use them.

No MetaTrader5 Python package, no DLL imports, no ZeroMQ dependency. The bridge is a native MQL5 Expert Advisor using MQL5's built-in socket API, and it connects **out to backs2** — MQL5 sockets are client-only, so the C# side owns the listener.

```
+-------------------------+   TCP (client)   +--------------------------+
|     MT5 Terminal        |  127.0.0.1:5555  |       backs2 Engine      |
|  Backs2TickBridge.mq5   | ---------------> |  Mt5TickServer (C#)      |
|  (Expert Advisor)       | <--------------- |  (TcpListener)           |
+-------------------------+                  +--------------------------+
                                                         |
                                                         v
                                             +----------------------+
                                             |  Mt5TickService      |
                                             |  (req/resp by id)    |
                                             +----------------------+
```

## Setup

### 1. MT5 terminal settings

**Tools -> Options -> Expert Advisors:**

- [x] `Allow algorithmic trading`
- [x] `Allow DLL imports` — the native socket API does not require this, but some MT5 builds gate `SocketCreate` behind it.
- [x] `Allow WebRequest for listed URL` — **add `127.0.0.1` to the list.** The WebRequest whitelist governs `SocketCreate`/`SocketConnect`. Without the entry, the EA's socket calls fail with `SocketConnect FAILED, error 5273`.
- [ ] `Disable algorithmic trading when the account has been changed` — **uncheck.** On OANDA demo, MT5 flips AlgoTrading off on every login when this is enabled, and the EA silently never runs.
- [ ] `Disable algorithmic trading when the profile has been changed` — **uncheck.**

Click OK. Then click the **Algo Trading** button in the top toolbar — it must be green.

### 2. Install the bridge EA

1. In MT5: **File -> Open Data Folder**.
2. Navigate to `MQL5\Experts\`.
3. Copy `docs\Backs2TickBridge.mq5` from the backs2 project into this folder.
4. In MT5, press **F4** to open MetaEditor.
5. In MetaEditor, open `Backs2TickBridge.mq5` and press **F7** to compile. The Errors tab should read `0 errors, 0 warnings`.
6. Back in MT5: right-click **Navigator -> Refresh**.
7. Drag `Backs2TickBridge` from the Navigator onto **any live chart** in the main MT5 window (not the Strategy Tester).
8. **No properties dialog will appear** — the EA has no inputs. This is expected.
9. Check the **Experts** tab at the bottom. You should see:

```
>>> Backs2TickBridge: OnInit ENTRY
>>> Backs2TickBridge: CONNECTED to 127.0.0.1:5555
```

### 3. Start backs2

`dotnet run` in the project root. The log should show:

```
[INF] [Provider] MT5: server listening on 127.0.0.1:5555
```

The C# listener must be up before the EA connects. If you started backs2 second, the EA retries every 3 seconds and connects on the next attempt.

## Extraction subtab

### Fetch parameters

Every parameter lives in the MT5 Ticks tab. The EA has no inputs.

| Field | Meaning |
|---|---|
| **Symbol** | The broker-exposed symbol name. On OANDA, gold is `GOLD.pro`, not `XAUUSD`. Check Market Watch — the tooltip on each row shows the internal name. |
| **Lookback (h)** | Window start = `now - LookbackHours`. `0` sends `from_msc=0` and the EA falls back to a 24-hour window. |
| **Target ticks** | Upper bound on accumulated ticks. Fetch ends when reached or when the broker runs out of history. Max 50,000,000. |
| **Batch size** | Ticks requested per TCP round trip (1000–50000, default 20000). The EA loops `CopyTicks` internally in 2000-tick slices, so one round trip carries the whole batch. |
| **Populate Grid** | Uncheck for large fetches to skip DataGrid fills (CSV streaming continues, no UI freeze). Above ~500k ticks, keep it off. **Apply to Fetch** unchecks it automatically when the verified count exceeds 500k. |

### Fetch flow

1. Click **Ping** — the status line should turn green with the round-trip latency in milliseconds.
2. Enter the **Symbol** exactly as your broker exposes it.
3. Set **Lookback (h)**, **Target ticks**, and **Batch size**.
4. Click **Fetch Ticks**. The DataGrid fills in batch by batch (if **Populate Grid** is on) and every batch is appended to a streaming CSV at `data/ticks/{symbol}_{stamp}.csv`.
5. Click **Save CSV** to persist the in-memory grid contents as a one-shot CSV.

The fetch tracks its actual range (start, end, count) so the **Verify Range** panel can cross-check it.

### Verify Range panel

Confirms that a fetched CSV covers exactly the tick range you think it does, by asking the terminal to count ticks in the range natively and comparing against the last fetch.

- **From / To** — timestamps in `YYYY/MM/DD HH:mm:ss[.mmm]` form, interpreted at the **UTC offset** you set (range -12 … +14, half-hour steps). A live preview shows the resolved UTC range. **To is exclusive.**
- **Load Last Fetch Range** — fills From/To from the last fetch's actual window.
- **Verify** — sends a `COUNT_TICKS` request to the EA. Reports the MT5 tick count, the first/last tick timestamps and how far they sit inside the range, then compares the count against the last fetch: `✓ MATCH` or `✗ MISMATCH` with the signed delta.
- **Apply to Fetch** — copies the verified range into the fetch parameters: Lookback is derived from From, Target ticks is auto-sized to the verified count plus ~1% headroom (capped at 50M, and **Populate Grid** is switched off above 500k). Symbol and batch size are preserved.

Use it when a fetch was interrupted, when stitching multiple CSVs, or when a downstream parse shows gaps at the seams.

## Requester subtab

Forces the terminal to download a specific tick range from the broker and write it into the local `.tkc` cache. No raw ticks cross the wire — the data lands on disk in MT5's own monthly cache format, ready for the **.tkc Extract** subtab to decode.

### Parameters

| Field | Meaning |
|---|---|
| **Symbol** | The broker-exposed symbol name. Same rules as Extraction. |
| **Lookback (Days)** | Window start = `now - ReqLookbackDays`. Default 3652 (10 years). |

### Flow

1. Enter the symbol and lookback.
2. Click **Request Sync in MT5**.
3. The progress bar advances once per day processed. The status line shows `day D/T (P%) | synced S, skipped K` — synced = terminal answered with tick data, skipped = terminal returned a hard error or timed out retrying.
4. On completion, the status line reports `Sync Complete! Synced: … | Skipped: … | Total: … day(s).`

### Behavior notes

- The EA emits an immediate-ack PROGRESS frame the moment it accepts the request, before the first `CopyTicksRange` call blocks on a broker-side history download.
- Each day-chunk is one `CopyTicksRange` call. On `ERR_HISTORY_TIMEOUT (4403)` with zero ticks, the EA retries the same day on the next 10 ms timer tick, up to 30 seconds, then skips it.
- The C# request timeout is span-scaled: `Mt5TimeoutMs + clamp(spanDays × 500 ms, 20 min, 60 min)`. A 10-year sync gets ~30 minutes of headroom.
- A new SYNC or COUNT_TICKS request pre-empts an in-flight one. Latest-wins.
- Typical wall time on a cold symbol: the first day's `CopyTicksRange` is the slow one — the terminal may spend 30 s to a few minutes downloading history from the broker. Subsequent days run at terminal disk speed.

## .tkc Extract subtab

Reads MetaTrader's local monthly tick-cache files (`{TerminalDataRoot}\bases\{Broker}\ticks\{Symbol}\YYYYMM.tkc`) directly from disk and aggregates them into kline cache.

1. Enter **Terminal Data Root** (the `Open Data Folder` path), **Broker Folder**, **Symbol**.
2. Pick the **Timeframe**.
3. Optional: **Lookback (Days)** and **Max Ticks** (0 = all).
4. Click **Extract to Cache**.

The decode iterates files newest-first and ticks within each file in reverse, so lookback and tick limits stop the decode mid-file. Bars are built from `Bid`, `Volume` = tick count. Zero-bid ticks are dropped. The result is written to `data/cache/{symbol}_{timeframe}.json` with source `MT5 Parsed`.

## Parsing subtab

Converts a tick CSV produced by the Extraction subtab into OHLCV bars and writes them into the standard Kline cache, so the dataset behaves like any other: it appears in the Data tab with source `MT5 Parsed`, and it can be charted, queued, backtested, and fed to Monte Carlo.

1. Click **Select MT5 CSV...** — the picker starts in `data/ticks` and filters `*.csv`. The symbol is taken from the filename (the part before the first `_`).
2. Pick the **Target Timeframe** (any canonical timeframe from `TimeframeMap.All`).
3. Click **Parse to Cache**.

Rules of the conversion:

- Bars are built from the **Bid** price. Open/High/Low/Close come from the bid ticks in each bucket; **Volume is the tick count** in the bucket.
- Ticks with a zero bid are dropped. Up to 25 dropped timestamps are listed in a Warn log line; above that only the count is logged.
- Buckets follow UTC calendar boundaries for the chosen timeframe.
- The result is saved to `data/cache/{symbol}_{timeframe}.json` with cache source `MT5 Parsed`. Re-parsing the same symbol/timeframe overwrites the previous file.

Note that tick-count volume is not comparable to provider volume (contract lots). Volume-relative logic (spikes vs an average) works fine; absolute levels do not.

## Protocol

Newline-delimited JSON over TCP. One request per connection message, one response per request. Every request carries a monotonically increasing `id`; the EA echoes it back. Progress frames use `id:0` and carry the originating request's id under `request_id`.

### Request

```
{"id":1,"action":"PING"}
{"id":2,"action":"GET_TICKS","symbol":"GOLD.pro","from_msc":1700000000000,"count":20000}
{"id":3,"action":"GET_TERMINAL_INFO"}
{"id":4,"action":"COUNT_TICKS","symbol":"GOLD.pro","from_msc":1700000000000,"to_msc":1700003600000,"chunk_ms":21600000}
{"id":5,"action":"SYNC_TICKS_RANGE","symbol":"GOLD.pro","from_msc":1700000000000,"to_msc":2000000000000}
```

`COUNT_TICKS`'s `to_msc` is exclusive and must be greater than `from_msc`. The optional `chunk_ms` switches COUNT_TICKS into chunked mode (one chunk per `OnTimer` tick, PROGRESS between chunks).

`SYNC_TICKS_RANGE`'s `to_msc` is exclusive. The EA iterates one day at a time from `from_msc` to `to_msc`, forcing the terminal to fetch each day's tick history from the broker.

### Response

```
{"id":1,"status":"OK"}
{"id":2,"status":"OK","count":20000,"next_from_msc":1700000001234,"ticks":[...]}
{"id":3,"status":"ERROR","message":"CopyTicks returned -1, error 4401"}
{"id":4,"status":"OK","count":18342,"first_msc":1700000000089,"last_msc":1700003599987,"perf_us":4211,"chunks":1}
{"id":5,"status":"OK","synced_days":362,"skipped_days":3,"total_days":365}
```

### Progress frames

Emitted as unsolicited messages between chunks/days. `id` is always 0; the originating request's id is in `request_id`.

```
{"id":0,"action":"PROGRESS","request_id":4,"chunks_done":3,"chunks_total":6,"cumulative_ticks":9821}
{"id":0,"action":"PROGRESS","request_id":5,"chunks_done":42,"chunks_total":365,"synced_days":41,"skipped_days":1}
```

The C# side parses both shapes with the same reader (`Mt5TickServer.TryParseProgress`). `cumulative_ticks`, `synced_days`, and `skipped_days` are all optional — a missing field means "not applicable for this action". They all surface through the same `CountProgress` event as `Mt5CountProgress`.

### Tick fields

| Field | Type | Notes |
|---|---|---|
| `time_msc` | long | Unix milliseconds |
| `bid` | double | |
| `ask` | double | |
| `last` | double | **Zero for forex/metals** |
| `volume` | ulong | **Zero for forex/metals** |
| `volume_real` | double | **Zero for forex/metals** |
| `flags` | uint | Bitmask: Bid=2, Ask=4, Last=8, Volume=16, Buy=32, Sell=64 |

## Config tab settings

| Setting | Default | Meaning |
|---|---|---|
| `Mt5Host` | `127.0.0.1` | Bridge host. Changing this also requires editing `#define BRIDGE_HOST` in `docs/Backs2TickBridge.mq5` and recompiling. |
| `Mt5Port` | `5555` | Bridge port. Same caveat — edit `#define BRIDGE_PORT` to match. |
| `Mt5TimeoutMs` | `15000` | Per-request timeout for `GET_TICKS` / `PING` / `PREWARM`. Must exceed the EA's worst-case wall time for a batched response (~1000 ms for 20,000 ticks). `SYNC_TICKS_RANGE` and chunked `COUNT_TICKS` compute their own timeouts on top of this base. |

## Known constraints

- **2000-tick cap per `CopyTicks` call.** The EA paginates internally; one TCP round trip carries up to 50,000 ticks.
- **First-call broker sync.** A cold symbol's first `CopyTicks` can block up to 45 seconds while the terminal downloads tick history from the trade server. Raise `Mt5TimeoutMs` to 60000 for a cold symbol, or use **Prewarm** behavior (fired automatically before the first fetch per symbol per process).
- **SYNC first-day stall.** The first `CopyTicksRange` in a `SYNC_TICKS_RANGE` request is the one that triggers the broker-side download. Expect 30 s to a few minutes on the first day of a cold symbol. Subsequent days run at terminal disk speed.
- **Inclusive `from_msc`.** Batches overlap at boundary milliseconds; both sides dedupe the trailing same-ms count. Never fetch adjacent ranges and concatenate without overlap handling — use **Verify Range** to confirm seams.
- **Forex/metals ticks have empty `last`/`volume`/`volume_real`.** Bid/ask movement is the signal. Zero values in those columns are expected, not a bug.
- **No DOM.** `SYMBOL_TICKS_BOOKDEPTH` is 0 for OTC CFD instruments like retail forex and metals. Order book data is not implemented and is not viable for these instrument types.
- **Symbol naming is broker-specific.** OANDA exposes gold as `GOLD.pro`. `CopyTicks("XAUUSD", ...)` on OANDA returns `error 4401`.
- **Strategy Tester is blocked.** The MT5 Strategy Tester sandbox rejects all network calls. The EA only works on a live chart.
- **Zero-bid ticks exist.** Mostly at the daily broker-maintenance rollover (~00:05 UTC). The parser drops them; a daily zero-bid tick in a CSV is normal.

## Architecture notes

- `Mt5TickService` is a plain singleton, **not** an `IDataProvider`. Tick data has a different shape (bid/ask/last/flags) and no timeframe, so it does not fit the Kline-centric provider contract.
- `Mt5TickServer` owns the `TcpListener`. It matches responses to requests by echoing `id`. One client at a time — the EA reconnects on drop.
- CSV streaming during a fetch uses `Mt5TickStreamWriter`; **Save CSV** uses the one-shot `Mt5TickWriter`. Both live in `src/core/Writers/`.
- The Parsing subtab writes Kline data through `CacheManager` (source `MT5 Parsed`) — parsed bars are ordinary cached datasets, not tick data.
- Log tag is `Provider`. No new `LogTag` enum value.
- No NuGet package beyond the BCL. ZeroMQ was evaluated and rejected.