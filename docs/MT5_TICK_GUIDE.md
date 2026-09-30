# MT5 Raw Tick Data Guide

## Overview

The **MT5 Ticks** tab fetches raw `MqlTick` data from a running MetaTrader 5 terminal over a local TCP bridge. No MetaTrader5 Python package, no DLL imports, no ZeroMQ dependency. The bridge is a native MQL5 Expert Advisor using MQL5's built-in socket API, and it connects **out to backs2** — MQL5 sockets are client-only, so the C# side owns the listener.

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
8. **No properties dialog will appear** — the EA has no inputs. This is expected and confirms version 1.06 loaded.
9. Check the **Experts** tab at the bottom. You should see:

```
>>> Backs2TickBridge v1.06: OnInit ENTRY
>>> Backs2TickBridge: CONNECTED to 127.0.0.1:5555
```

### 3. Start backs2

`dotnet run` in the project root. The log should show:

```
[INF] [Provider] MT5: server listening on 127.0.0.1:5555
```

The C# listener must be up before the EA connects. If you started backs2 second, the EA retries every 3 seconds and connects on the next attempt.

### 4. Use the tab

Open the **MT5 Ticks** tab in backs2.

1. Click **Ping** — the status line should turn green with the round-trip latency in milliseconds.
2. Enter the **Symbol** exactly as your broker exposes it. On OANDA, gold is `GOLD.pro`, not `XAUUSD`. Check Market Watch — the tooltip on each row shows the internal name.
3. Set **Lookback (h)** — how many hours back to start fetching. 24 is a good default.
4. Set **Target ticks** — how many ticks to accumulate. The fetch stops when this is reached or the broker runs out of history, whichever comes first.
5. Set **Chunk size** — ticks per round trip. 250 keeps the progress bar smooth. Do not exceed 2000 (MQL5's `CopyTicks` cap).
6. Click **Fetch Ticks**. The DataGrid fills in batch by batch.
7. Click **Save CSV** to persist to `data/ticks/{symbol}_{stamp}.csv`.

## Protocol

Newline-delimited JSON over TCP. One request per connection message, one response per request. Every request carries a monotonically increasing `id`; the EA echoes it back.

### Request

```
{"id":1,"action":"PING"}
{"id":2,"action":"GET_TICKS","symbol":"GOLD.pro","from_msc":1700000000000,"count":250}
{"id":3,"action":"GET_TERMINAL_INFO"}
```

### Response

```
{"id":1,"status":"OK"}
{"id":2,"status":"OK","count":250,"next_from_msc":1700000001234,"ticks":[...]}
{"id":3,"status":"ERROR","message":"CopyTicks returned -1, error 4401"}
```

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

## Fetch parameters

Every parameter lives in the MT5 Ticks tab. The EA has no inputs.

| Field | Meaning |
|---|---|
| **Lookback (h)** | Window start = `now - LookbackHours`. `0` sends `from_msc=0` and the EA falls back to a 24-hour window. |
| **Target ticks** | Upper bound on accumulated ticks. Fetch ends when reached or when the broker runs out of history. |
| **Chunk size** | Ticks requested per round trip. Smaller = smoother progress, more round trips. Hard cap is 2000. |

## Config tab settings

| Setting | Default | Meaning |
|---|---|---|
| `Mt5Host` | `127.0.0.1` | Bridge host. Changing this also requires editing `#define BRIDGE_HOST` in `docs/Backs2TickBridge.mq5` and recompiling. |
| `Mt5Port` | `5555` | Bridge port. Same caveat — edit `#define BRIDGE_PORT` to match. |
| `Mt5TimeoutMs` | `15000` | Per-request timeout. Must exceed the EA's worst-case serialization time (~5 s for a 2000-tick batch). Raise to `60000` for the first fetch of a cold symbol. |

## Known constraints

- **2000-tick cap per `CopyTicks` call.** Automatic pagination via `next_from_msc`.
- **First-call broker sync.** A cold symbol's first `CopyTicks` can block up to 45 seconds while the terminal downloads tick history from the trade server. Raise `Mt5TimeoutMs` to 60000 in the Config tab, restart backs2, and retry.
- **MQL5 string concatenation is O(n^2).** A 2000-tick response serializes in about 5 seconds. Chunk size 250 keeps each batch under 600 ms and enables streaming UI updates.
- **Forex/metals ticks have empty `last`/`volume`/`volume_real`.** Bid/ask movement is the signal. Zero values in those columns are expected, not a bug.
- **No DOM.** `SYMBOL_TICKS_BOOKDEPTH` is 0 for OTC CFD instruments like retail forex and metals. Order book data is not implemented and is not viable for these instrument types.
- **Symbol naming is broker-specific.** OANDA exposes gold as `GOLD.pro`. `CopyTicks("XAUUSD", ...)` on OANDA returns `error 4401` (ERR_HISTORY_NOT_FOUND).
- **Strategy Tester is blocked.** The MT5 Strategy Tester sandbox rejects all network calls (`SocketCreate FAILED, error 4014`). The EA only works on a live chart.

## Architecture notes

- `Mt5TickService` is a plain singleton, **not** an `IDataProvider`. Tick data has a different shape (bid/ask/last/flags) and no timeframe, so it does not fit the Kline-centric provider contract. It is consumed only by the MT5 Ticks tab.
- `Mt5TickServer` owns the `TcpListener`. It matches responses to requests by echoing `id`. One client at a time — the EA reconnects on drop.
- The CSV writer lives in `src/core/Writers/` — a home for writers that do not fit the standard `CacheEnvelope<KlineCompact[]>` shape produced by every existing provider. `CacheManager` remains the writer for all Kline data.
- Log tag is `Provider` (already covers anything data-source-related). No new `LogTag` enum value.
- No new NuGet package. Uses `System.Net.Sockets.TcpListener` / `TcpClient` from the BCL.
- ZeroMQ was evaluated and rejected. For one EA on localhost talking to one C# process, raw TCP has no functional downside and no dependency cost.