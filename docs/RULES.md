<!-- docs\RULES.md -->
ONLY SPEAK IN ENGLISH

# Development Rules
- ALWAYS check namespace before writing code (backs2.Core for logic, backs2.ViewModels for UI).
- ViewModels: public partial class inheriting ObservableObject.
- XAML: always set x:Class="backs2.Views.YourView" and x:DataType for compiled bindings.
- Keep file paths shallow.
- Style: High-Performance C# — minimal allocations, List<T> over LINQ in hot paths.
- Architecture:
    - No static global variables. Enforce xManager/DI.
    - All I/O non-blocking (Async/Await).
    - UI progress via IProgress<T>.
    - Strictly typed bindings required for all XAML.
- Metrics:
    - Every `IMetric` must return a defined value for degenerate inputs and must never throw.
    - Metrics are pure and stateless.
    - `MetricRegistry.GetMetric` returns `NullMetric.Instance` on a miss.
    - New metrics: subclass `BaseMetric`, supply `Name`, `Lag`, `Category`, `Description`, register in `MetricRegistry`.
- Settings & Dynamic UI:
    - To add an engine setting, declare a public property on `AppSettings`. It auto-renders in the Config tab.
    - Supported types (`int`, `double`, `bool`, `string`, `Color`) auto-render.
    - Do NOT hardcode XAML controls for `AppSettings` properties.
    - For non-secret on-disk state, resolve the config root via `ConfigManager.ConfigDirectory`.
- Secrets:
    - Never hard-code API keys. Never write secrets to config/settings.json.
    - Read from ConfigManager read-only properties, sourced from .env.
- Providers:
    - Implement IDataProvider; register as keyed transient in App.axaml.cs.
    - Symbol resolution inside the provider. Timeframe mapping inside TimeframeMap.
    - Return an empty List<Kline> on failure; never throw.
    - Name is human-readable and shown in the Config tab.
- Caching:
    - Disk format is `CacheEnvelope` wrapping `KlineCompact[]`, serialized via `KlineJsonContext`.
    - Never add a new cache format without a schema/version detection path in `LoadCacheAsync`.
    - Cache keys are `symbol@timeframe`.
- Strategies:
    - Live in data/strategies/, one file per strategy, top-level statements.
    - Globals: Engine, Data, Log, Trades, Params.
    - Parameters declared at the top of the file with `// @param <Name> <Type> = <Default> [options: "A", "B"]`.
    - Never place a strategy .cs outside data/strategies/.
    - Trade recording: `Trades.Advance(index)` before `Enter` / `Exit`. `Enter(side, price, size = 1.0)`.
- Presets:
    - Live in config/presets/{StrategyName}_{PresetName}.preset.json.
    - Manage exclusively through PresetService.
- Accounts:
    - One JSON per firm at data/accounts/{Firm}.account.json.
    - Manage exclusively through AccountService.
    - Active selection is a triple in settings.json.
    - Never split a firm across multiple files.
- Selection Queue:
    - `SelectionQueue` is the only place enabled-dataset state lives.
    - `DataRowViewModel.IsQueued` is the only writer.
    - Queue modal pending-removal is committed on Done, discarded on Cancel.
    - Group-header checkbox in the Data tab bulk-toggles every *visible* row in that group. Writes still go through per-row `IsQueued`, one `SelectionQueue.Add/Remove` per member row. Never write the group header itself to the queue.
    - Runner accepts exactly one ticker at a time. Multi-ticker queue = hard refuse (status warning + `CanRun = false`). Single-ticker queue runs exactly the queued timeframes — never silently expand to all cached timeframes for the ticker.
- Monte Carlo:
    - Operates in the time domain. Never scramble trades directly.
    - Build `TimeSlice[]` via `TimeSliceBuilder.Build(trades, klines)`. One slice per Kline.
    - Block bootstrap: stationary (Politis & Romano), geometric block length, circular wrap.
    - Compound as `CurrentBalance += RealizedPnl`. Never percentage-compound. Prop firm capital is nominal.
    - Evaluate prop-firm rules per slice against MTM low, not closed-trade PnL.
    - `DrawdownType` from the active account: `static`, `trailing-eod`, `trailing-intraday`. Never ignore it.
    - Batch: independent runs per queued dataset. Aggregate by summing expectations, not by pooling trades.
- Logging:
    - Write via `LogManager.Info(LogTag.X, msg)` / `Warn` / `Debug` / `Error`.
    - `LogManager` is static. Never register it in DI. Never pass it as a ctor parameter.
    - Tags: App, Cache, Data, Provider, Strategy, Runner, Account, MonteCarlo, Overview, Chart, Config, Metric.
    - Debug for troubleshooting-only lines. Info for main flow. Warn for recoverable abnormal. Error for exceptions with `ex`.
- XAML binding inside templates:
    - Use Binding DataContext.Prop with RelativeSource AncestorType=UserControl.
    - Do not use `$parent[UserControl].((vm:Type)DataContext).Prop`.
- GUI theming:
    - All colors, sizes, spacing, and fonts come from `src/gui/Theme/Tokens.axaml`. Reference tokens via `{DynamicResource TokenName}` in XAML.
    - Never hardcode hex codes in view XAML. The tokens are the single source of truth for the palette.
    - The shell (custom title bar, left nav rail, status bar) lives in `MainWindow.axaml` and consumes the same tokens. Do not add chrome-specific colors outside the token file.
    - Navigation commands go in `src/gui/MainViewModel.Shell.cs`. They set `ActiveTabIndex` and do nothing else.
    - Fullscreen is set from `MainWindow.Opened` and re-applied from `Activated` when the state returns to `Normal`. Never use `WindowState="Maximized"` — DWM applies rounded corners to Maximized + ExtendClientArea windows on Windows 11.
    - Theme colors live on `AppSettings` as `Theme*Color` ARGB hex strings. Add a new themable color by declaring the property, adding a default in `ConfigManager.LoadConfig` and in `Tokens.axaml`, and mapping it in `ThemeApplier.Apply`. The Config tab auto-renders it in the Theme Colors expander.
    - Never use WPF's `ColorPicker` or `ColorView` in this app. Both exhibit first-click lazy-load freezes and blank-render bugs in 11.0.x (WPF discussions #9128 / #9098). The supported picker is `src/gui/Config/ColorPickerWindow.xaml`, built from `ColorSpectrum` + `ColorSlider` + `ColorPreviewer`.
    - Clicking OK in the color picker applies the theme live via `ConfigViewModel.ApplyThemePreview`. Persistence still requires the Save Settings button. Do not auto-save from the color dialog.
- Charting:
    - ScottPlot.WPF 5.0.55 is the standard.
- Tab host & ViewModel resolution:
    - Views must not resolve their own ViewModels in the constructor. `Application.Current is App app` → `app.Services.GetRequiredService<TViewModel>()` inside a View ctor is forbidden — it forces eager construction of every tab's ViewModel and its transitive singleton dependencies at startup.
    - `MainViewModel` exposes one lazily-resolved property per tab (`OverviewVm`, `RunnerVm`, ...). `CurrentVm` is a switch on `ActiveTabIndex` returning the correct property; `OnActiveTabIndexChanged` raises `PropertyChanged(nameof(CurrentVm))`.
    - `MainWindow.axaml` uses a headers-only `TabControl` (empty `TabItem` elements) plus one sibling `ContentControl` bound to `CurrentVm`. Never place `<views:XView />` directly inside a `TabItem` — that re-introduces eager construction for every tab.
    - VM→View mapping lives exclusively in `App.axaml` DataTemplates, one per tab ViewModel.
    - View code-behind is `InitializeComponent()` plus View-local wiring that does not depend on a specific ViewModel instance. Anything that touches the DataContext goes in `OnDataContextChanged`.
- Tab load latency:
    - Tab ViewModel constructors must not block the UI thread on file I/O, JSON parsing, or directory enumeration. If a ViewModel needs expensive data to display, kick it off via a fire-and-forget `Task` in the ctor and populate the `ObservableCollection`s when the work completes.
    - `ObservableCollection<T>` mutations must stay on the UI thread. Do CPU-bound work on the thread pool (via `Task.Run` or `Parallel.ForEach`), then dispatch results back. `ConfigureAwait(true)` on the outer await when the continuation touches collections.
    - `Parallel.ForEach` over a list of independent files is the standard pattern when per-file work is CPU-heavy. Protect any shared accumulator with a lock.
    - Add a `_refreshing` flag on any refresh command whose work can overlap itself. Duplicate scans waste cycles and can race with collection clears.
- Build vs Run Protocol:
    - Iteration (compile-time, not commit-ready): `dotnet build 2>&1 | Select-Object -Last 15` - incremental, ~4s on 11.3.x. Pre-commit verification: `dotnet build --no-incremental -p:RunAnalyzersDuringBuild=true 2>&1 | Select-Object -Last 15`. Success ends with `Build succeeded`.
    - Build + run (runtime changes): `dotnet run`, then after close: `Get-ChildItem logs\ | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content`.
    - Ask for build OR run, never both.
    - If the app crashed before `LogManager.Initialize`, there is no new log file — ask for terminal output instead.

## Data Automation
- Window discovery uses EnumWindows + PID match. No injection, no hooks.
- Input uses SendInput via P/Invoke. Never a NuGet input library.
- Capture uses PrintWindow with PW_RENDERFULLCONTENT. Never DXGI Desktop Duplication.
- Both calibrations (region, click point) persist to config/data_automation.json, which is git-ignored.
- Run order: focus+maximize -> click -> 200x right-arrow -> capture+left-arrow loop.
- Output: data/shots/{symbol}_{timeframe}_{index:D4}.png.
- The view's code-behind owns the calibration overlay. Do not extract it to a standalone .axaml.
- Do not commit config/data_automation.json.

## MT5 Tick Bridge
- Transport is raw TCP via BCL `System.Net.Sockets`. Never add ZeroMQ, NetMQ, or any other IPC dependency. One EA, one C# listener, localhost — the BCL covers it.
- The MT5 EA is a TCP **client**. It connects out to a C# `TcpListener`. MQL5 does not implement `SocketBind`/`SocketListen`/`SocketAccept` — do not attempt to make the EA a server.
- The EA has **no inputs**. Host, port, timeout, and `CopyTicks` cap are `#define` constants. Every fetch parameter (symbol, lookback, target, batch size) is commanded from C#.
- **Inclusive-boundary rule (critical).** `CopyTicks`'s `from_msc` is inclusive. Never write `next_from_msc = lastMsc + 1` — it silently drops every same-ms tick at every seam. Send `next_from_msc = lastMsc` and drop exactly `trailingCount` leading ticks on the next slice, where `trailingCount` = number of consecutive same-`time_msc` ticks at the tail of the last emitted slice. Both the EA (internal `CopyTicks` loop) and C# (`_lastEmittedTimeMsc` / `_lastEmittedTrailing`) enforce this. A change on one side must be mirrored on the other.
- **Raw-count exit check.** The broker-ran-out-of-history exit uses the **raw** tick count from the EA, before C# dedupe. Comparing the deduped count against the requested batch size falsely trips on every batch after the first.
- EA serialization must use the `uchar[]` byte-buffer path (`AppendStr` + `SocketSend`). Never use MQL5 `string` concat on the hot path — both `+=` and `StringAdd` reallocate on every append and are measured O(N²): 20,000 ticks took 39.9 s to serialize in v1.08; the byte-buffer path does the same in ~14 ms.
- EA-side batching: the EA accepts `count` up to `MAX_REQUEST_TICKS = 50000`. It loops `CopyTicks` internally in 2000-tick slices, chaining via `next_from_msc` inside EA RAM. Never raise `MAX_REQUEST_TICKS` without raising `Mt5TimeoutMs` — a 50000-tick batch at ~50 µs/tick CopyTicks is ~2500 ms plus serialization.
- `CopyTicks` has a 2000-tick hard cap per call. Never increase the per-call chunk size above 2000.
- Default batch size is **20000** ticks per TCP round trip.
- Prewarm is per-symbol per-process. `Mt5TickService.PrewarmAsync` is idempotent via `HashSet<string> _warmedSymbols`. Do not bypass it — the first `CopyTicks` per symbol costs ~2× warm (tick-DB page-in). Dropping prewarm costs ~30% of first-fetch throughput.
- `Mt5TickService.FetchTicksAsync` takes an `onBatch` callback (`Func<IReadOnlyList<Mt5Tick>, Task>?`) that is awaited inside the fetch loop. This is deliberate backpressure: a slow disk write blocks the socket read so memory stays bounded. Never change it to fire-and-forget.
- `Mt5TickService.CountTicksAsync` uses an adaptive timeout: `Mt5TimeoutMs + min(spanDays, 90) × 3000 ms`. The base timeout is sized for 20k-tick fetch batches, not multi-day range queries — a 30-day query takes ~44 s. Never send a fixed-timeout COUNT_TICKS request without the extension.
- CSV streaming during a fetch uses `Mt5TickStreamWriter`, not `Mt5TickWriter.WriteAsync`. `Mt5TickStreamWriter.OpenAsync` + `AppendAsync` per batch + `CompleteAsync`. `Mt5TickWriter.WriteAsync` is the one-shot writer behind the tab's **Save CSV** button.
- `Mt5TickService` and `Mt5TickServer` are registered as singletons in `App.axaml.cs`. `Mt5TickViewModel` is transient and takes `(Mt5TickService, ConfigManager, CacheManager)`. Never register tick infrastructure as an `IDataProvider`.
- `LogTag.Provider` is the tag for all MT5 logs. Do not add an MT5-specific tag.
- `src/core/Writers/` holds writers that do not fit the `CacheEnvelope<KlineCompact[]>` shape. Today: `Mt5TickWriter.cs` (both `Mt5TickWriter` and `Mt5TickStreamWriter`). Do not move `CacheManager` there.
- MT5 side requires `127.0.0.1` whitelisted in **Tools → Options → Expert Advisors → Allow WebRequest for listed URL**. Do not skip this — the WebRequest list governs `SocketCreate`/`SocketConnect`.
- The EA must be attached to a live chart in the main MT5 window. The Strategy Tester sandbox blocks network calls (`SocketCreate FAILED, error 4014`).
- Symbol names are broker-specific. OANDA exposes gold as `GOLD.pro`, not `XAUUSD`. `CopyTicks("XAUUSD", ...)` on OANDA returns `error 4401`.
- `Mt5TimeoutMs` must exceed the EA's worst-case wall time for a batched fetch response. A 20000-tick batch is ~1000 ms end-to-end. Default 15000 has headroom; raise to 60000 for the first fetch of a cold symbol (broker sync).
- `data/ticks/` is git-ignored. Never commit fetched tick CSVs.
- Performance telemetry (`perf_copy_us`, `perf_ser_us`) is returned by the EA in every `GET_TICKS` response. Do not remove — the C# log's `ea_copy` / `ea_ser` split is the only way to tell terminal-side vs EA-side cost without re-instrumenting.
- **The MT5-side wall is `CopyTicks`.** ~35–60 µs/tick warm, ~50 µs/tick cold, ~0.03 µs/tick in the 4,096-tick fast access cache. No bridge change can beat the terminal's disk-read floor for historical backfill.

### MT5 SYNC_TICKS_RANGE (v1.17+)
- `SYNC_TICKS_RANGE` forces the terminal to download a tick range from the broker into the local `.tkc` cache. It is a broker-side write, not an in-C# fetch. The C# side never sees ticks from this path.
- The EA iterates day-by-day via `CopyTicksRange`. On `ERR_HISTORY_TIMEOUT (4403)` with zero ticks it retries the same day on the next `OnTimer` tick, up to `SYNC_PER_DAY_TIMEOUT_MS` (30 s). Any non-timeout return — including a successful zero-tick response — advances the cursor.
- **Immediate ack is mandatory.** `HandleSyncTicksRange` must emit an initial `PROGRESS` frame the moment the request is accepted, throttle bypassed (`g_sync_last_prog_ms = 0`). The first `CopyTicksRange` on a cold symbol can block for minutes while the terminal downloads history; without an immediate ack, the C# side has no confirmation the request landed and will time out on a live request.
- **Timeout must be span-scaled, not the default fetch timeout.** `Mt5TickService.SyncTicksRangeAsync` computes `Mt5TimeoutMs + clamp(spanDays × 500 ms, 20 min, 60 min)`. A 10-year sync gets ~30 min of headroom. Do not use `Mt5TimeoutMs` alone, and do not copy the COUNT_TICKS adaptive-timeout formula — the two actions have different wall-time profiles.
- **PROGRESS frame shapes differ by action.** `Mt5TickServer.TryParseProgress` must accept BOTH `cumulative_ticks` (COUNT_TICKS) and `synced_days` / `skipped_days` (SYNC_TICKS_RANGE). None of them is mandatory — a missing field means "not applicable for this action", not "malformed message". Defaulting to 0 is the correct behavior. Do not re-introduce a hard requirement on `cumulative_ticks`.
- `Mt5CountProgress` carries `RequestId, ChunksDone, ChunksTotal, CumulativeTicks = 0, SyncedDays = 0, SkippedDays = 0`. Do not fork it into per-action records — one record with action-dependent defaults keeps the single `CountProgress` event on `Mt5TickServer`.
- `SyncTicksRangeAsync` and `CountTicksAsync` both take an `IProgress<Mt5CountProgress>?`. Attach the handler as a plain `Action<Mt5CountProgress>` on `_server.CountProgress` (add/remove around the request); never subscribe a `Progress<T>` to a service event — `Progress<T>` is a consumer of reports, not a raiser.
- The final SYNC response carries `synced_days`, `skipped_days`, `total_days`. The final COUNT response carries `count`, `first_msc`, `last_msc`, `perf_us`, `chunks`. Do not mix the two shapes.
- A new chunked request (SYNC or COUNT) pre-empts any in-flight one (latest-wins). Do not attempt to multiplex.

### MT5 Grid Cap & Populate Grid
- `Mt5TickViewModel.MaxGridRows = 100_000`. `PopulateGrid` auto-unchecks once the collection hits the cap; the fetch itself is unaffected — CSV streaming continues through the channel.
- `OnTargetTicksChanged` auto-unchecks `PopulateGrid` when `TargetTicks > 500_000` and shows a banner. Guards against the freeze class: the user typing a huge number and forgetting to uncheck.
- Never raise `MaxGridRows` without re-validating the DataGrid freeze behavior. The freeze was a `Reset`-per-batch storm (one `NotifyCollectionChangedAction.Reset` per batch, DataGrid re-evaluating the whole collection on each). It was not a memory issue.
- `GridCapText` is the only banner. Show it when the grid is capped or auto-disabled; hide it when empty.

### MT5 CSV → Cache Parsing
- The parse **must** run inside `Task.Run`. Never inline on the UI thread — a 20M-line CSV would freeze the app.
- Bars are built from `Bid`, `Volume` = tick count. Never use `Ask` or `Last` — forex/metals `Last` and `Volume` are empty.
- Zero-bid ticks (`bid <= 0`) are dropped. OANDA emits one at the daily session open. Retained for the log capped at 10,000 samples.
- Bucketing is epoch-relative: `(timeMsc / tfMs) * tfMs`. Never re-introduce per-hour anchoring — it breaks 45m (produced alternating 45- and 15-minute bars at hour boundaries). `1D` = UTC midnight; `1W` = ISO Monday UTC. Documented limitation: parsed daily/weekly bars are UTC-aligned, MT5 charts align to server day.
- Symbol comes from the CSV filename before the first `_`. Never parse it from a column.
- Cache source is **`MT5 Parsed`**. Written through `CacheManager.SaveCacheAsync("{symbol}@{timeframe}", klines, "MT5 Parsed")`. Never write to a different location or bypass the cache.
- `MT5 Parsed` is cache-only — never fetched over the network. It appears in the Providers tab and the Data tab like any other source.

### MT5 .tkc Cache Extraction (Phase 16)
- Decoder lives in `src/core/MT5/tck/` — `TckModels.cs`, `TkcDecoder.cs`, `TkcFile.cs`. `TkcRepository.ListSymbolFiles(root, broker, symbol)` enumerates `{root}\bases\{broker}\ticks\{symbol}\YYYYMM.tkc`.
- `.tkc` format: magic `0x000001FD` at offset 0; symbol as UTF-16LE in `[0xA0, 0xD0)`; index starts at `0x00E4`, 48 bytes per entry, terminated by `size == 0` or non-monotonic `offset`; day payload is a sequence of interleaved zlib streams beginning with `0x78` + one of `0x01 | 0x5E | 0x9C | 0xDA`.
- `InflateZlib` must be given the full remaining `[offset..offset+size)` — `ZLibStream` self-terminates at the zlib footer. Never cap the input slice; larger streams silently truncate.
- `ClassifyBlock` must check for a `Timestamps` block **first** (first 8 bytes decode as Unix-ms in `[1.42e12, 2.0e12]`). Do not check zero-fraction first — delta-encoded timestamps can be 0 for many consecutive ticks, which previously caused valid timestamp blocks to be tagged `Empty` and whole days to be dropped.
- A day's blocks are grouped `[Timestamps, Prices, Prices, ...]`. `DecodeFromBlocks` must walk forward and decode each group independently — never pick the single largest `Timestamps` block globally. `202610.tkc` has 105 blocks in one day; the global-pick bug silently dropped 95% of its ticks.
- Per-record layout is 16 bytes: timestamps are delta-encoded (first record absolute Unix-ms, subsequent `u64` deltas); bid/ask are f64 in the first **or** second half of the record, detected per block by `DetectPriceOffset`. Never hard-code the offset.
- Decode is **serial across files**. Never parallel — the full-history GOLD.pro set holds ~3.6 GB of ticks in RAM if decoded in parallel. Peak memory must stay bounded by the largest single file's tick list plus the bucket dict.
- Iterate files newest-first, ticks within a file in reverse. This lets `TkcLookbackDays` and `TkcMaxTicks` stop the decode mid-file without materialising every tick. Bucket semantics under reverse iteration: **first** bid seen in a bucket is **Close**, **last** bid seen is **Open**. High/Low are symmetric.
- `FileOverlapsLookback` cheap-parses `YYYYMM.tkc` filenames to skip whole months before the cutoff. Non-standard filenames are conservatively included (decoded, then filtered tick-by-tick).
- Bars are built from `Bid`, `Volume` = tick count. Zero-bid ticks (`Bid <= 0`) are dropped and counted.
- Timeframe bucketing is epoch-relative: `(timeMsc / tfMs) * tfMs`. Same rule as the CSV parser. `1D` = UTC midnight, `1W` = ISO Monday UTC.
- Cache source is **`MT5 Parsed`**. Written through `CacheManager.SaveCacheAsync("{symbol}@{timeframe}", klines, "MT5 Parsed")`.
- Logging: exactly **one** `Info` line per file (`TKC 'file': N ticks, D day(s), T ms`) and at most one `Warn` per run (zero-bid drop count). Never add per-day, per-block, or per-stream logging to the decoder. The last time this rule was violated, 60 lines/file of diagnostics buried a silent-tick-loss bug for hours.
- The `MT5 Ticks` tab is an optional source: decoded bars feed any Kline consumer (Chart, Runner, Monte Carlo) exactly like a CSV-parsed dataset.
- Never run `.tkc` ticks directly through a strategy or metric. `RunnerScriptGlobals.Data` is `List<Kline>`, `IMetric.Calculate` takes `List<Kline>`, `TimeSliceBuilder` produces one slice per Kline, and `CacheManager` stores `KlineCompact[]`. Always aggregate to bars first — running on raw ticks would need a parallel execution engine (new globals type, tick-aware metrics, tick-aware TradeRecorder, tick-aware Monte Carlo) and would silently break bar-based indicators (RSI-14 over 14 ticks, ATR = 0, etc.).

## Commit Convention
When a phase is completed, ship a commit with the message format:

    <type>(<scope>): <summary>

    - <bullet 1>
    - <bullet 2>

Types: feat, fix, perf, docs, refactor, chore, wip.
Scope: providers, cache, strategies, logs, data, chart, config, metrics, gui, runner, presets, accounts, overview, montecarlo, queue, mt5, writers.

Summary: one line, imperative mood, no trailing period. Under 72 characters.

After the commit, print the last three log lines:

    git log --oneline -3
    git status

- **Commit messages MUST have detailed descriptions.** Every commit must include:
    - A clear, descriptive title (type(scope): summary)
    - A body with detailed explanation of what changed and why
    - Bullet points for each significant change
    - Verification steps that were performed
    - Any notes or caveats

  Example of a good commit message:
  ```
  fix(gui): resolve dark theme background issue on all views
  
  ## Summary
  Fixed the dark theme background not applying correctly to all UserControls
  after the Avalonia to WPF migration. The issue was caused by missing
  Background properties in several views.
  
  ## Changes
  - Added Background to ContentControl in MainWindow.xaml
  - Added Background to all UserControl templates
  - Verified all views render correctly with dark theme
  
  ## Verification
  - Build: PASSED (0 errors, 0 warnings)
  - Visual: all views render with correct dark theme
  ```
  
  Bad example (too vague):
  ```
  fix: some issues
  ```
  
  The working tree must be clean.
