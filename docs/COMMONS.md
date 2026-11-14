# COMMONS.md — Common Issues in AI-Generated Code (backs2 Edition)

Purpose: a catalogue of problems that AI coding agents repeatedly introduce or leave behind in a
project shaped like backs2 (C# / .NET 8, desktop GUI via Avalonia/WPF-style XAML, CommunityToolkit.Mvvm,
Roslyn scripting, ScottPlot, ComputeSharp, MT5 TCP bridge, JSON disk cache, Monte Carlo engine).

This file is a HYPOTHESIS LIST. Nothing in here is confirmed to exist in the repo.
An auditing agent must verify each entry against the real code and only promote
CONFIRMED findings (with file path, line, evidence) into `realissues.md`.

Entry format:
- ID: stable identifier (C-###)
- Symptom: what you observe at runtime or in review
- Why AI does it: the generation habit that causes it
- Detect: how to find it in this repo (grep / read / measure)
- Fix: the project-conformant remedy

Severity legend: S1 = data loss / crash / wrong results, S2 = serious perf or correctness risk,
S3 = maintainability or minor perf, S4 = cosmetic.

---

# PART A — PERFORMANCE

## A1. Allocation and GC pressure

### C-001 LINQ in hot loops (S2)
- Symptom: GC gen0 storms during Optimize, Monte Carlo, metrics over 600k bars.
- Why AI does it: LINQ is the idiomatic default in training data.
- Detect: `Select-String -Path src\core\**\*.cs -Pattern '\.Select\(|\.Where\(|\.ToList\(|\.OrderBy\(|\.Sum\(|\.Any\('`
- Detect (focus): metrics, TimeSliceBuilder, SimulationKernelCpu, CpuBackend, TradeRecorder, Parsing loops.
- Fix: for-loops over List<T>/arrays; project rule says "List<T> over LINQ in hot paths".
- Note: LINQ in one-time UI setup is fine; only flag per-bar / per-iteration paths.

### C-002 ToList()/ToArray() defensive copies (S3)
- Symptom: duplicated 600k-element lists, memory spikes.
- Why AI does it: avoids "collection modified" worries without analysis.
- Detect: grep `ToList()`, `ToArray()`, `new List<Kline>(` on already-materialized data.
- Fix: pass IReadOnlyList<T> or the existing list; copy only at ownership boundaries.

### C-003 String concatenation in loops (S3)
- Symptom: slow log formatting, slow CSV writing.
- Why AI does it: `+=` is shortest to write.
- Detect: grep `+= "` and `+ ","` inside for/foreach bodies.
- Fix: StringBuilder, or write directly to a stream writer.

### C-004 string.Split / Substring in parsers (S2)
- Symptom: CSV tick parsing allocates a string per field per row (20M-line files).
- Why AI does it: simplest parsing approach.
- Detect: Mt5 parsing partial, CSV readers, `.Split(',')` in loops.
- Fix: ReadOnlySpan<char>, manual index scanning, `double.TryParse(span)`.

### C-005 Boxing of value types (S3)
- Symptom: hidden allocations when structs go through object, non-generic collections, or string.Format.
- Why AI does it: uses `object` params, `string.Format`, non-generic interfaces.
- Detect: grep `object ` params, `string.Format(`, `ArrayList`, `Hashtable`, non-generic `IEnumerable`.
- Fix: generics, interpolated handlers, typed APIs.

### C-006 Closures capturing in hot paths (S3)
- Symptom: per-call delegate allocation in per-bar callbacks.
- Why AI does it: lambdas are convenient.
- Detect: lambdas inside loops capturing locals; `Parallel.For(..., i => ...)` bodies creating objects.
- Fix: static lambdas, hoist captured state, pass state arguments.

### C-007 Per-iteration `new Random(seed + i)` cost (S3)
- Symptom: allocation of one Random per Monte Carlo iteration.
- Why AI does it: simplest deterministic seeding.
- Detect: CpuBackend, SimulationKernelCpu, CpuComputeDispatcher.
- Fix: acceptable for determinism contract; if it profiles hot, use a struct PRNG (SplitMix64/xoshiro) seeded from (seed, index). Must keep determinism contract in ARCHITECTURE §22.

### C-008 Struct vs class misuse for Kline/TimeSlice (S3)
- Symptom: List<Kline> where Kline is a class → pointer chasing, 600k heap objects.
- Why AI does it: records default to class.
- Detect: definition of Kline, Trade, KlineCompact.
- Fix: readonly record struct for dense data; arrays of structs for kernels. TimeSlice is already a struct — keep.

### C-009 Large Object Heap fragmentation (S2)
- Symptom: working set 1.5–3 GB, long gen2 pauses.
- Why AI does it: allocates `new double[n]` per call without pooling.
- Detect: arrays ≥85,000 bytes allocated per version/iteration (curves, result arrays, slices).
- Fix: ArrayPool<T>, reuse buffers per worker, cap retained curves (MaxCurveCells).

### C-010 Retaining everything "just in case" (S2)
- Symptom: results hold equity curves, terminal balances, and source data for every dataset in RAM.
- Why AI does it: keeps all data so later features "just work".
- Detect: LastRunStore, OptimizationVersion, MonteCarloResult retention.
- Fix: downsample, drop on new run, store on disk, keep scalars only.

### C-011 Missing capacity on collections (S4)
- Symptom: repeated List growth when final size is known.
- Detect: `new List<T>()` followed by an Add loop over a known-size source.
- Fix: `new List<T>(count)`.

### C-012 Substring/ToString for keys (S3)
- Symptom: cache key `symbol + "@" + timeframe` built on every lookup.
- Detect: DataManager, CacheManager, SelectionQueue.Contains.
- Fix: struct key (Symbol, Timeframe) or cache the key string in the entry.

### C-013 DateTime allocations/conversions per bar (S2)
- Symptom: TimeZoneInfo conversion per bar (already seen: EasternTime, 60k calls/version).
- Why AI does it: calls the BCL conversion directly everywhere.
- Detect: grep `ConvertTimeFromUtc`, `ToLocalTime`, `DateTimeOffset.FromUnixTimeMilliseconds` in loops.
- Fix: precomputed transition tables, work in Unix ms, convert at the edge.

### C-014 Enum.ToString / Enum.Parse in loops (S4)
- Detect: grep `.ToString()` on enums in tight code; `Enum.Parse`.
- Fix: switch expressions or cached lookup arrays.

### C-015 Regex constructed per call (S3)
- Detect: `new Regex(` or `Regex.IsMatch(` in methods called often.
- Fix: static readonly Regex with `RegexOptions.Compiled`, or [GeneratedRegex].

### C-016 Exceptions used for control flow (S2)
- Symptom: slow parsing with first-chance exception spam.
- Detect: try/catch around `int.Parse`, `double.Parse`, `Dictionary[key]`.
- Fix: TryParse, TryGetValue.

### C-017 JSON serialization via reflection (S2)
- Symptom: slow cache load, trimming problems.
- Detect: `JsonSerializer.Serialize/Deserialize` without a JsonSerializerContext.
- Fix: source-generated contexts (KlineJsonContext, AccountJsonContext pattern).

### C-018 Re-reading files already in memory (S3)
- Detect: `File.ReadAllText` for the same path repeated; strategy source read per combination.
- Fix: cache by (path, mtime, size); the project already does this for compile cache.

### C-019 Defensive `Clone()`/copy of Dictionary params per version (S3)
- Detect: Params dictionaries copied per combination when read-only would do.
- Fix: IReadOnlyDictionary, share immutable instances.

### C-020 Sorting when selection suffices (S3)
- Detect: `OrderBy(...).Take(n)` or `Sort` on 100k arrays to get a top-N.
- Fix: partial selection / heap for top-N; sort only when full order is needed (percentiles).

## A2. CPU-bound work and parallelism

### C-021 Nested parallelism over-subscription (S2)
- Symptom: Parallel.For inside Parallel.For; thread starvation, poor scaling.
- Why AI does it: parallelizes every layer independently.
- Detect: CpuBackend.RunIterationsAsync, OptimizationRunner, CpuComputeDispatcher.
- Fix: exactly one owner of parallelism; inner calls use MaxDegreeOfParallelism = 1 (already a project rule).

### C-022 Parallel.For for tiny work (S3)
- Detect: Parallel.For over < ~1000 trivial items.
- Fix: sequential loop under a threshold.

### C-023 False sharing between worker results (S3)
- Symptom: poor scaling when threads write adjacent array slots.
- Detect: per-thread counters stored in a shared int[]/double[] indexed by thread id.
- Fix: thread-local accumulators merged at the end, or padding.

### C-024 Lock contention on shared accumulators (S2)
- Symptom: CPU well below 100% under parallel load (22% average was seen in Phase 22E).
- Detect: `lock (` inside Parallel bodies; shared Dictionary/List with lock; LogManager under heavy load.
- Fix: thread-local aggregation, ConcurrentDictionary with GetOrAdd, lock-free counters via Interlocked.

### C-025 Synchronous stat/IO inside parallel loops (S3)
- Detect: `File.GetLastWriteTimeUtc`, `new FileInfo(path).Length` per combination.
- Fix: stat once per batch, pass the descriptor down.

### C-026 Redundant recomputation across combinations (S2)
- Detect: metrics, time-zone tables, indicator series recomputed for each parameter set despite identical inputs.
- Fix: precompute shared series once per dataset, memoize pure functions by input hash, per-run scoped.

### C-027 Poor chunk sizing (S3)
- Detect: chunk constants (McChunkSize, GpuIterationBatch) not tuned to measurement.
- Fix: measure; keep chunk size independent from results (determinism contract).

### C-028 Using Task.Run as a parallelism primitive (S3)
- Detect: `Task.Run` in a loop for N items, then `WhenAll`.
- Fix: Parallel.ForEachAsync with a degree cap, or Channels.

### C-029 Busy-wait / polling loops (S2)
- Detect: `while (...) { await Task.Delay(10) }`, `Thread.Sleep`, spin loops.
- Fix: events, TaskCompletionSource, SemaphoreSlim, Channels.

### C-030 Timers firing faster than useful (S3)
- Detect: DispatcherTimer intervals < 50 ms; per-line UI posts.
- Fix: coalesce; the Logs tab pattern (bounded queue + one flush) is the model.

## A3. GPU

### C-031 GPU path slower than CPU for small dispatches (S2)
- Symptom: GPU "acceleration" loses because of per-dispatch overhead.
- Why AI does it: assumes GPU is always faster.
- Detect: GpuBackend usage at coarse-pass shapes; check threshold (400k iterations).
- Fix: gate on total work; measure with --mc-bench; keep CPU-batch for small shapes.

### C-032 Per-call device buffer allocation (S2)
- Detect: `GraphicsDevice.AllocateReadOnlyBuffer/AllocateReadWriteBuffer` inside per-chunk or per-call paths.
- Fix: allocate once per batch, reuse across dispatches.

### C-033 Readback of data that is not needed (S3)
- Detect: nine output arrays read back when the caller needs one aggregate.
- Fix: reduce on the device or request only needed arrays.

### C-034 Missing TDR safety (S1)
- Symptom: Windows resets the GPU driver on long dispatches.
- Detect: single dispatch covering >100k iterations × long slices.
- Fix: chunk dispatches; observe cancellation between dispatches.

### C-035 GPU determinism claims that are false (S3)
- Detect: docs/tests asserting bit-identical CPU vs GPU results.
- Fix: statistical equivalence tests; document per-backend determinism only.

### C-036 Unprotected first-dispatch race (S2)
- Symptom: ComputeSharp pipeline cache duplicate-key exception under parallel first use.
- Detect: kernel dispatch without a first-dispatch gate.
- Fix: per-device gate (already shipped in 29B.2; verify it still holds).

## A4. IO and disk

### C-037 Synchronous IO on async paths (S2)
- Detect: `File.ReadAllText`, `File.WriteAllText`, `.Result`, `.Wait()` inside async methods.
- Fix: async APIs with FileOptions.Asynchronous, 64 KB buffers.

### C-038 Unbuffered stream writes (S2)
- Detect: StreamWriter without buffer size, flush per line, `FileStream` default 4 KB buffers in bulk writers.
- Fix: larger buffers, flush per batch.

### C-039 Directory enumeration repeated (S3)
- Detect: `Directory.GetFiles` each refresh with full metadata parse.
- Fix: memoize metadata on (path, mtime, length) (project already added; verify invalidation).

### C-040 Loading whole file to inspect a header (S3)
- Detect: reading entire cache JSON to learn bar count/range.
- Fix: header sidecar or streaming scan.

### C-041 No bounded backpressure (S2)
- Detect: producer/consumer with unbounded Channel/Queue; fire-and-forget writes.
- Fix: bounded Channel; awaiting `onBatch` (project mandates backpressure).

### C-042 Temp file leaks (S3)
- Detect: Path.GetTempFileName without delete; partial writes left after cancel.
- Fix: write to `.tmp` then atomic rename; cleanup in finally.

### C-043 Non-atomic cache writes (S1)
- Symptom: half-written JSON after crash → refetch or corrupted dataset.
- Detect: CacheManager.SaveCacheAsync writing directly to the final path.
- Fix: temp + File.Move(overwrite: true).

### C-044 Long paths and deep folders (S3)
- Detect: generated nested folders; agent rules require flat paths.
- Fix: flat file names with delimiters.

---

# PART B — UI, MVVM, XAML

## B1. UI-thread discipline

### C-045 Blocking the UI thread (S1)
- Symptom: window "Not responding" on tab open.
- Why AI does it: puts load logic in constructors.
- Detect: ViewModel constructors calling directory scans, JSON parsing, `.Result`.
- Fix: fire-and-forget async init, populate on completion, `_refreshing` guard.

### C-046 ObservableCollection mutated off-thread (S1)
- Symptom: random InvalidOperationException or silent corruption.
- Detect: `Add` inside `Task.Run` or Parallel bodies.
- Fix: compute off-thread, marshal batches to UI (Dispatcher.UIThread.Post / StreamingLoader).

### C-047 Dispatcher post per item (S2)
- Detect: `Dispatcher.UIThread.Post` inside loops; `Progress<T>` reporting thousands of times.
- Fix: ThrottledProgress at 100 ms, bounded queue + coalesced flush.

### C-048 CollectionChanged storms (S2)
- Detect: Add in a loop on a bound collection; Reset per batch.
- Fix: RangeObservableCollection.AddRange, replace the collection once.

### C-049 Layout thrash from frequent text updates (S3)
- Detect: StatusText assigned on every iteration; TextBox.Text appended per log line.
- Fix: throttle updates; rolling bounded buffer.

### C-050 Rebuilding entire grids on filter keystrokes (S2)
- Detect: filter handlers calling ApplyView immediately.
- Fix: debounce 150 ms (already added; verify all filters use it).

### C-051 Missing virtualization (S2)
- Detect: ItemsControl in ScrollViewer with hundreds of rows instead of virtualizing panel; ItemsControl for large lists.
- Fix: DataGrid/ListBox virtualization, VirtualizingStackPanel.

### C-052 Heavy converters/bindings evaluated per frame (S3)
- Detect: Converters doing allocation; bindings to computed properties that rebuild collections.
- Fix: cache computed values, raise PropertyChanged only on change.

### C-053 Visual-tree duplication (S3)
- Detect: three plots stacked and toggled via IsVisible is OK; but creating controls per switch is not.
- Fix: keep controls, toggle visibility, cache render markers.

### C-054 Frame hooks that cost (S3)
- Detect: per-frame handlers doing more than an Interlocked.Increment.
- Fix: keep per-frame cost near zero; sample at 1 Hz.

## B2. MVVM Toolkit pitfalls

### C-055 Partial property generated but not partial class (S2)
- Symptom: AXSG/CS errors, phantom CS1061 on generated members.
- Detect: [ObservableProperty] in a class missing `partial`.
- Fix: `public partial class`; clean obj/bin before diagnosing (project rule).

### C-056 [ObservableProperty] on public field with wrong naming (S3)
- Detect: field names not camelCase/underscore → generated property collisions.
- Fix: `_fieldName` convention.

### C-057 Manual PropertyChanged alongside generated (S3)
- Detect: SetProperty plus attribute for the same member.
- Fix: choose one.

### C-058 Command CanExecute not refreshed (S2)
- Symptom: buttons stay disabled/enabled incorrectly (e.g., CanRun after queue changes).
- Detect: [RelayCommand(CanExecute=...)] without [NotifyCanExecuteChangedFor] or manual NotifyCanExecuteChanged.
- Fix: add attributes or call the notifier on dependency changes.

### C-059 Async commands with no cancellation or reentrancy guard (S2)
- Detect: AsyncRelayCommand running twice concurrently; no `IsBusy`.
- Fix: AllowConcurrentExecutions=false, CTS per run, latest-wins.

### C-060 Messenger leaks and wrong registration (S3)
- Detect: `WeakReferenceMessenger` register without IRecipient; strong references in event handlers.
- Fix: IRecipient<T> + Register(this); unsubscribe events in teardown.

### C-061 Event subscriptions never removed (S2)
- Symptom: memory growth after tab switches; Transient VMs subscribed to singleton events.
- Detect: `+=` to LastRunStore.Changed, AccountService.ActiveChanged, LogManager.Logged without matching `-=` / dispose.
- Fix: IDisposable on VM, unsubscribe; or weak events; singleton lifetime for tab VMs if intended.

### C-062 Transient ViewModels resolved repeatedly (S3)
- Detect: DataTemplate resolution creating a fresh VM each tab switch.
- Fix: MainViewModel lazy `_xxx ??=` pattern (already used) — verify nobody bypasses it.

### C-063 View resolving its own ViewModel (S2)
- Detect: `Application.Current is App app` + `GetRequiredService` inside View ctor.
- Fix: DataTemplate mapping + OnDataContextChanged.

## B3. XAML

### C-064 Missing x:DataType / wrong namespace (S2)
- Symptom: compiled binding errors, cascading "Compiled binding path is invalid".
- Detect: Views without x:DataType; xmlns:vm / xmlns:views not matching folders.
- Fix: set x:DataType on every root; treat CS#### errors first (rule).

### C-065 Wrong RelativeSource syntax in templates (S2)
- Detect: `$parent[UserControl].((vm:Type)DataContext).Prop`.
- Fix: `{Binding DataContext.Prop, RelativeSource={RelativeSource AncestorType=UserControl}}`.

### C-066 Hardcoded colors/sizes (S3)
- Detect: `#[0-9A-Fa-f]{6,8}` in *.axaml/*.xaml outside Tokens.
- Fix: `{DynamicResource TokenName}`; add token + ThemeApplier mapping.

### C-067 Framework confusion (WPF vs Avalonia) (S2)
- Symptom: docs/code mix WPF (`DataContext`, `.xaml`, `x:Class="backs2.Views..."`) and Avalonia (`.axaml`, `Dispatcher.UIThread`, `avares://`).
- Why AI does it: both look similar and docs in repo disagree with themselves.
- Detect: file extensions, `using System.Windows` vs `using Avalonia`, `Application.Current.Dispatcher` vs `Dispatcher.UIThread`.
- Fix: pick the framework actually referenced in backs2.csproj; update docs and code to be consistent.

### C-068 Deprecated/renamed API after upgrade (S2)
- Detect: `SystemDecorations`, `SetTextAsync`, obsolete clipboard/drag APIs, compiler warnings CS0618.
- Fix: modern replacements; treat obsolete warnings as work items.

### C-069 Controls known to freeze/blank (S2)
- Detect: ColorPicker/ColorView usage anywhere.
- Fix: ColorPickerWindow with primitives (project rule).

### C-070 Inline tab content (S2)
- Detect: `<views:XView />` inside TabItem.
- Fix: tree/ContentControl + DataTemplates.

### C-071 Fixed widths clipping headers (S4)
- Detect: Width="..." on DataGrid column headers with sort glyphs.
- Fix: MinWidth, auto/star sizing, sort-icon width tokens.

### C-072 DataGrid with many template columns (S3)
- Detect: every column as DataGridTemplateColumn with nested panels.
- Fix: text columns where possible; keep templates light.

### C-073 Focus/keyboard bugs on plots (S3)
- Detect: Plot without Focusable, UserInputProcessor not disabled, KeyDown on wrong element.
- Fix: Focusable=True, tunnelled handler, Disable() as documented.

### C-074 DPI/scaling blur (S3)
- Detect: Chart not refreshed on ScalingChanged; fixed pixel coordinates in code.
- Fix: re-render on ScalingChanged; use RenderScaling for pixel conversions.

### C-075 Plot rebuilt on each toggle (S3)
- Detect: Plot.Clear + re-add on mode switch without cache marker.
- Fix: render-cache markers per mode and dataset, invalidate on new run.

---

# PART C — ASYNC, CONCURRENCY, LIFETIME

### C-076 async void outside event handlers (S1)
- Symptom: unobserved exceptions crash the process.
- Detect: grep `async void`.
- Fix: async Task; wrap fire-and-forget in a helper that logs exceptions.

### C-077 Fire-and-forget without exception logging (S2)
- Detect: `_ = SomethingAsync();` with no try/catch.
- Fix: `SafeFireAndForget` helper → LogManager.Error.

### C-078 Missing ConfigureAwait semantics (S3)
- Detect: library-style core code awaiting without ConfigureAwait(false) where UI affinity is not needed; UI code continuing on a pool thread by accident.
- Fix: core: ConfigureAwait(false); VM: keep context when touching collections.

### C-079 .Result / .Wait() / GetAwaiter().GetResult() (S1)
- Symptom: deadlocks, UI freezes.
- Detect: grep these tokens.
- Fix: async all the way; only allowed in Main or tests.

### C-080 CancellationToken ignored (S2)
- Detect: async methods accepting ct but not passing it to inner calls or loops.
- Fix: thread ct through; `ThrowIfCancellationRequested` at loop heads; observe between chunks.

### C-081 CTS not disposed / not replaced (S3)
- Detect: `new CancellationTokenSource()` stored in field without Dispose of previous.
- Fix: cancel + dispose + replace, latest-wins.

### C-082 Cancellation throwing out of top-level (S2)
- Detect: OperationCanceledException unhandled in command handlers; logged as Error.
- Fix: catch and report as cancelled (Info).

### C-083 Shared mutable state without synchronization (S1)
- Detect: static Dictionary/List, singleton caches mutated from multiple threads.
- Fix: ConcurrentDictionary, lock, immutable snapshots, Interlocked.

### C-084 Double-checked locking implemented wrongly (S2)
- Detect: non-volatile flags, check-then-act on Dictionary.
- Fix: Lazy<T>, ConcurrentDictionary.GetOrAdd with Lazy values (single-flight).

### C-085 Check-then-act on files (S3)
- Detect: `if (!File.Exists) File.Create`.
- Fix: handle exceptions; use atomic operations.

### C-086 Semaphore not released in finally (S1)
- Detect: `Wait`/`WaitAsync` without try/finally Release.
- Fix: try/finally always.

### C-087 Throttle semaphore serializes more than intended (S2)
- Detect: StrategyManager SemaphoreSlim(1,1) used in optimizer parallel path.
- Fix: bypass for optimizer (documented), keep for single-run path; verify it still is.

### C-088 Event handlers raised under lock (S3)
- Detect: `Changed?.Invoke` inside lock(_gate).
- Fix: snapshot, release, then invoke.

### C-089 Timer/Task leaks on shutdown (S2)
- Detect: background loops with no stop signal; processes (workers) not killed.
- Fix: CTS on exit, IDisposable, Job Object kill-on-close for workers.

### C-090 Child process handling (S1)
- Detect: Process.Start without redirect draining → deadlock; no kill on cancel.
- Fix: async read of stdout/stderr; Kill(entireProcessTree: true); Job Object.

### C-091 Pipe framing bugs (S1)
- Detect: reading frames with a single `Read` call; no loop for partial reads.
- Fix: ReadExactly; validate length prefix against MaxFrameBytes.

### C-092 TaskCompletionSource without RunContinuationsAsynchronously (S2)
- Symptom: reader loop executing continuations inline → stalls.
- Detect: `new TaskCompletionSource<...>()` in Mt5TickServer.
- Fix: TaskCreationOptions.RunContinuationsAsynchronously.

### C-093 Request/response id collisions (S2)
- Detect: pending-request dictionaries cleaned up only on success.
- Fix: remove on timeout/cancel/disconnect; fail all pending on socket drop.

### C-094 Timeouts missing on network/pipes (S1)
- Detect: awaits on socket/HTTP/pipe without timeout or ct.
- Fix: linked CTS with CancelAfter; resilience pipeline for HTTP.

### C-095 Reconnect storms / no backoff (S3)
- Detect: loops that retry immediately.
- Fix: exponential backoff with jitter.

### C-096 Thread-affinity assumptions in Roslyn/ComputeSharp (S2)
- Detect: shared ScriptRunner or GraphicsDevice objects used without proven thread safety.
- Fix: verify documented thread safety; per-thread instances otherwise.

### C-097 Static state hiding in "services" (S2)
- Detect: `static` fields outside LogManager (project forbids).
- Fix: DI singleton.

### C-098 Service locator creeping in (S3)
- Detect: `Services.GetRequiredService` outside composition root/lazy VM resolution.
- Fix: constructor injection.

### C-099 Captive dependencies (S2)
- Symptom: singleton holding transient/scoped state or UI objects.
- Detect: singleton constructors taking VMs/Views.
- Fix: invert dependencies; messenger or events.

### C-100 Disposal missing (S3)
- Detect: IDisposable types (FileSystemWatcher, CTS, HttpClient handlers, GraphicsDevice buffers, Process) not disposed.
- Fix: using/await using; DI-owned disposal for singletons.

---

# PART D — DATA, CACHE, PROVIDERS

### C-101 Provider fallback swallowing real errors (S2)
- Symptom: every provider fails silently; user sees "no data".
- Detect: empty `catch {}` / catch returning empty list with no log.
- Fix: log Warn with provider name and reason; keep "never throw" contract but never be silent.

### C-102 Rate limits and 429 handling (S2)
- Detect: loops over symbols/timeframes without delay; no Retry-After handling.
- Fix: resilience handler config; per-provider rate limiter.

### C-103 Pagination off-by-one / duplicates / gaps (S1)
- Symptom: duplicate or missing bars at page seams.
- Detect: Binance 200-day windows, Tiingo/Massive ranges, FCS `length` windows.
- Fix: inclusive/exclusive boundaries documented; dedupe by timestamp; verify monotonic and gap report.

### C-104 Unsorted or duplicate timestamps accepted (S1)
- Detect: no sort/dedupe after merge; strategies assuming ascending order.
- Fix: normalize in DataManager boundary: ascending, unique, validated OHLC (H>=max(O,C), L<=min(O,C)).

### C-105 Timezone/DST assumptions (S1)
- Detect: `DateTime.Now`, `ToLocalTime`, unspecified-kind DateTimes, daily bars assumed to align with server day.
- Fix: Unix ms UTC everywhere in core; convert only for display; document daily alignment.

### C-106 Cache key collisions (S2)
- Detect: `symbol@timeframe` without normalization (case, whitespace, provider symbol aliases).
- Fix: canonical uppercase symbol, TimeframeMap canonical timeframe.

### C-107 Preferred-provider semantics wrong (S2)
- Detect: cache hit with different `source` returned when a specific provider was requested.
- Fix: honor `preferredProvider`; compare source on hit.

### C-108 Memory cache entry without size/expiry (S2)
- Detect: IMemoryCache.Set without SizeLimit/expiration; entries holding 600k bars each.
- Fix: SizeLimit, sliding expiry, compact representation.

### C-109 Cache schema evolution without version path (S1)
- Detect: new envelope fields added without LoadCacheAsync detection.
- Fix: versioned envelope, legacy loaders (Origin discriminator example).

### C-110 Deserializing then re-serializing to inspect (S3)
- Detect: metadata reads that fully deserialize.
- Fix: streaming Utf8JsonReader scan.

### C-111 Metadata memoization stale (S2)
- Detect: memo key missing mtime/length; memo not invalidated on SaveCacheAsync.
- Fix: key (path, mtime, length); explicit invalidate after save/delete.

### C-112 Volume heuristics wrong (S3)
- Detect: `HasVolume` computed from first bar only; zero volume treated as invalid for crypto (legit zero).
- Fix: scan semantics per documentation; provider-specific filters.

### C-113 Ignoring provider free-tier limits (S3)
- Detect: Finnhub candles on free tier returning empty; FCS length 500.
- Fix: surface Warn explaining the plan limit once per run.

### C-114 Symbol mapping in the wrong layer (S2)
- Detect: symbol/timeframe translation in DataManager or IDataProvider rather than the provider/TimeframeMap.
- Fix: move into provider and TimeframeMap (project rule).

### C-115 HttpClient misuse (S1)
- Symptom: socket exhaustion, DNS staleness.
- Detect: `new HttpClient()` per call; not using the named "ResilientClient".
- Fix: IHttpClientFactory named client.

### C-116 Secrets in logs/URLs (S1)
- Detect: logging full request URLs with apikey/token/access_key query parameters.
- Fix: redact query strings before logging.

### C-117 Key placeholder detection incomplete (S3)
- Detect: new key properties not passing through NormalizeValue.
- Fix: every key goes through NormalizeValue.

### C-118 Numeric parsing culture bugs (S1)
- Symptom: decimal comma locale breaks parsing/writing.
- Detect: `double.Parse(x)`, `ToString()` of doubles without CultureInfo.InvariantCulture.
- Fix: invariant culture everywhere in data paths, including CSV, logs, param strings.

### C-119 Float for prices (S2)
- Detect: `float` fields in OHLC; `decimal` mixed with `double` conversions.
- Fix: double in kernels (project decision); document precision limits.

### C-120 NaN/Infinity propagation (S1)
- Symptom: metrics or equity curves become NaN silently.
- Detect: divisions by High-Low, mean/std with zero count, percent returns on zero entry price.
- Fix: guard degenerate inputs; IMetric invariant "never throw, defined value".

### C-121 Partial fetch cached as complete (S1)
- Detect: SaveCacheAsync after truncated response; page failure mid-walk.
- Fix: only save complete ranges; mark incomplete; do not overwrite longer cache with shorter data.

### C-122 Overwriting richer cache with poorer (S1)
- Symptom: a refetch from a free provider replaces 600k MT5 bars with 5k.
- Detect: SaveCacheAsync without comparing bar counts/range/source.
- Fix: merge or protect by origin; prompt or refuse.

### C-123 Data origin confusion (S2)
- Detect: RawTick vs TimeframeFetch vs Legacy; queue consumers ignoring IQueueOriginGate.
- Fix: honor origin gate; surface warnings.

### C-124 Queue state drifting (S2)
- Detect: multiple writers to SelectionQueue; Provider = "Unknown" shown as truth.
- Fix: single writer (DataRowViewModel.IsQueued), restore re-derives origin.

### C-125 Stale UI after cache change (S3)
- Detect: Data/Providers tabs not refreshed after fetch/parse.
- Fix: latest-wins refresh; invalidate memo.

---

# PART E — STRATEGIES, ROSLYN, METRICS

### C-126 Strategy scripts compiled into the assembly (S1)
- Detect: backs2.csproj missing `<Compile Remove="data\**\*.cs" />`.
- Fix: restore the Compile Remove; verify build output.

### C-127 Compile cache key too weak (S2)
- Detect: key omits lastWriteUtc/size; or includes mutable parameter values (cache miss per combo).
- Fix: key by (path, lastWriteUtc, size); parameters passed at runtime via Params.

### C-128 Compile stampede (S2)
- Detect: N workers compile the same script on a cold cache.
- Fix: per-key single-flight gate with double-checked read (shipped Phase 31; verify intact).

### C-129 Script options leaking assemblies (S3)
- Detect: ScriptOptions referencing huge assembly sets, reflection-based metadata refs.
- Fix: minimal references and imports.

### C-130 Memory growth from ScriptState/collectible assemblies (S2)
- Detect: thousands of compiled scripts retained; no eviction.
- Fix: bounded cache, eviction on file change.

### C-131 Exceptions from user scripts crashing batch (S1)
- Detect: one bad combination aborting the whole optimize.
- Fix: per-version try/catch, record Error, continue.

### C-132 Infinite loops in scripts (S2)
- Detect: no per-run timeout/cancellation visible to scripts.
- Fix: cancellation token in globals, watchdog.

### C-133 Parameter parsing bugs (S2)
- Detect: StrategyParameterParser stopping at first non-comment line; `// @param` after `using` silently ignored.
- Fix: docs + validation warning when @param appears after code.

### C-134 Culture/format drift in Params (S2)
- Detect: Params values formatted with current culture ("0,5") parsed by scripts with invariant culture.
- Fix: invariant formatting for param values and presets.

### C-135 Grid builder float accumulation (S2)
- Symptom: Min..Max with step 0.1 produces 0.30000000000000004 and misses the end value.
- Detect: ParameterGridBuilder loops using `x += step`.
- Fix: index-based `min + i*step` with rounding; inclusive end tolerance.

### C-136 Combination explosion (S2)
- Detect: Cartesian product without cap before allocation.
- Fix: compute count with checked arithmetic first; refuse/sample above cap.

### C-137 Integer overflow in counts (S1)
- Detect: `int` for combinations × iterations × slices (curve cells).
- Fix: `long` and checked arithmetic (MaxCurveCells computations).

### C-138 Random sample not reproducible (S3)
- Detect: sampling with unseeded Random.
- Fix: seed from settings; log the seed.

### C-139 Trade recording invariants broken (S1)
- Detect: Enter without Advance; Exit without open position; overlapping positions.
- Fix: validate in TradeRecorder; surface errors rather than silently ignoring.

### C-140 Lookahead bias in strategies (S1)
- Symptom: unrealistically good backtests.
- Detect: strategy indexes `Data[i+1]`, uses current bar's close to decide fill on the same bar's open, metrics computed over the full series (`Calculate(Data)`) inside a per-bar loop.
- Fix: metrics over `Data.GetRange(0, i+1)` or incremental; fills on next bar.

### C-141 Metric calculated on the whole series per bar (S2)
- Symptom: O(n²) strategy runtime.
- Detect: `Engine.GetMetric("X").Calculate(Data)` inside a loop.
- Fix: precompute series once; add incremental metric API if needed.

### C-142 Metric invariants violated (S1)
- Detect: metrics throwing on empty list, dividing by zero, returning NaN.
- Fix: defined values for degenerate inputs; tests.

### C-143 Metric state (S2)
- Detect: instance fields caching last computed series.
- Fix: pure/stateless per rules.

### C-144 Volume profile bin edge errors (S3)
- Detect: max value lands in bin count (out of range); flat range.
- Fix: clamp once per candle; handle min==max.

### C-145 RSI/ATR Wilder seeding differences (S3)
- Detect: first average not SMA of first period; period alignment off by one.
- Fix: standard Wilder; unit test against known values.

### C-146 Name-based registry typos (S3)
- Detect: string metric names scattered in scripts; NullMetric hiding typos (returns 0 silently).
- Fix: Debug log on miss (once per name), constants.

### C-147 Backtest assumptions hidden (S2)
- Detect: no spread/commission/slippage; fills at close; fixed size 1.0.
- Fix: document assumptions; configurable costs.

### C-148 Equity/pnl units inconsistent (S1)
- Detect: PnL in price units vs percent vs money mixed; size scaling applied twice.
- Fix: explicit unit docs; test with known trades.

### C-149 Trades crossing session/EOD incorrectly (S2)
- Detect: EOD flag by day-of-year only (breaks at year boundary and timezone).
- Fix: use proper date comparison in the account's timezone.

### C-150 Strategy discoverability issues (S4)
- Detect: strategies outside data/strategies; names colliding with preset underscore rules.
- Fix: enforce location; sanitize underscores.

---

# PART F — MONTE CARLO, OPTIMIZER, COMPUTE

### C-151 Trade-domain shuffling (S1)
- Detect: any MC path that permutes trades.
- Fix: time-domain stationary block bootstrap only.

### C-152 Percent compounding (S1)
- Detect: balance *= 1+r in MC kernel.
- Fix: `CurrentBalance += RealizedPnl` (nominal).

### C-153 Rules evaluated on closed PnL, not MTM low (S1)
- Detect: drawdown checks after realizing.
- Fix: check MTM low first, then realize, then HWM, then EOD reset.

### C-154 DrawdownType ignored/misapplied (S1)
- Detect: `static`, `trailing-eod`, `trailing-intraday` collapsed into one branch.
- Fix: per-type HWM update; tests for each.

### C-155 Block bootstrap implementation errors (S2)
- Detect: block length not geometric; no circular wrap; mean block size off by one.
- Fix: p = 1/mean; wrap with modulo.

### C-156 Seed handling mistakes (S2)
- Detect: seed + chunk index instead of absolute iteration; Random.Shared use under determinism claims.
- Fix: absolute iteration index per RNG.

### C-157 Percentile computation errors (S2)
- Detect: off-by-one index, unsorted input, percentile on filtered subset.
- Fix: defined method (nearest-rank or linear), tests.

### C-158 Probabilities not summing to 1 (S2)
- Detect: P(ruin)+P(funded)+P(timeout) != 1 due to cancelled/truncated results.
- Fix: normalise by completed iterations; assert in debug.

### C-159 Truncated cancelled results reported as full (S2)
- Detect: UI shows probabilities from partial chunks without marking them.
- Fix: carry completed count; display "n of N iterations".

### C-160 Cap bypass (S2)
- Detect: MaxIterations/MaxCurveCells enforced in UI only.
- Fix: enforce in VM and engine (three layers, documented).

### C-161 Statistical overclaiming (S3)
- Detect: Edge % on <30 trades; MC confidence statements without CI.
- Fix: min-trade gates, report uncertainty.

### C-162 Overfitting through optimization (S1)
- Symptom: best parameter set is noise.
- Detect: ranking by in-sample PFunded only; refine survivors from same data.
- Fix: walk-forward OOS validation, parameter stability checks, penalise sparse trades.

### C-163 Multiple-testing blindness (S2)
- Detect: 500 combos, report best, no adjustment.
- Fix: display distribution; deflate or require OOS agreement.

### C-164 Coarse-vs-refine selection bias (S3)
- Detect: top-N chosen using noisy 100-iteration estimates.
- Fix: larger coarse iterations near the top; document.

### C-165 Memory spikes from per-version curves (S2)
- Detect: storing full curves per version; OOM on 500+ versions.
- Fix: downsample representative curve only (design already does this).

### C-166 Optimizer progress overhead (S2)
- Detect: Progress<T> per iteration.
- Fix: ThrottledProgress, coalesce.

### C-167 ETA misleading (S4)
- Detect: frozen projections; not scaled by outer parallelism.
- Fix: self-calibrating EMA per unit of work.

### C-168 Batch/optimization file growth (S3)
- Detect: data/optimizations grows unbounded; results tab reads all files each refresh.
- Fix: retention, index file, lazy load.

### C-169 Walk-forward leakage (S1)
- Detect: windows overlapping IS/OOS; parameters chosen using OOS data; step < OOS length with reuse.
- Fix: strict partitioning; embargo; tests for window planner.

### C-170 Walk-forward planner edge cases (S2)
- Detect: last partial window dropped silently; zero-length windows; mode (rolling vs anchored) confusion.
- Fix: explicit tests; log window plan.

---

# PART G — MT5 BRIDGE, NATIVE, IPC

### C-171 Inclusive-boundary bug (S1)
- Detect: `next_from_msc = lastMsc + 1` on EA or C#.
- Fix: send lastMsc; skip trailing same-ms ticks. Mirror both sides.

### C-172 Raw-count exit check using deduped counts (S2)
- Fix: use raw EA count for exhaustion test.

### C-173 MQL5 string concat on hot path (S2)
- Fix: uchar[] byte buffer with AppendStr.

### C-174 Timeout formula misuse (S1)
- Detect: SYNC using COUNT formula or fixed timeout; huge ranges timing out.
- Fix: span-scaled timeouts per action.

### C-175 Missing immediate ack for long actions (S1)
- Fix: emit initial PROGRESS right away (throttle bypass).

### C-176 PROGRESS parsing too strict (S2)
- Fix: only chunks_done/chunks_total required.

### C-177 Progress as event misuse (S3)
- Detect: subscribing Progress<T> to events.
- Fix: plain Action<T> handlers attached/detached around the request.

### C-178 Newline framing issues (S1)
- Detect: splitting on `\n` per read buffer, not accumulating partial lines; huge lines no cap.
- Fix: accumulate; cap line size; handle \r\n.

### C-179 One-client assumption (S3)
- Detect: new client replaces old without failing pending requests.
- Fix: fail pending on disconnect; log.

### C-180 Heartbeat vs liveness (S3)
- Detect: HasHeartbeat used as proof of responsiveness during long ops.
- Fix: document semantic; PROGRESS counts as liveness.

### C-181 .tkc decoder regressions (S1)
- Detect: global pick of largest timestamp block; hard-coded price offset; capped inflate input; zero-fraction check first.
- Fix: follow documented rules in ARCHITECTURE §20.

### C-182 Parallel decode memory blowup (S1)
- Detect: Parallel.ForEach over .tkc files.
- Fix: serial across files.

### C-183 Verbose decoder logging (S3)
- Detect: per-day/block/stream log lines.
- Fix: exactly one Info per file, ≤1 Warn per run.

### C-184 Bucket math drift (S1)
- Detect: per-hour anchoring; non-epoch-relative buckets; daily/weekly UTC alignment undocumented.
- Fix: `(timeMsc / tfMs) * tfMs`; document limitation.

### C-185 Open/close swap under reverse iteration (S1)
- Fix: first seen = Close, last seen = Open when iterating newest-first.

### C-186 Zero bid handling (S2)
- Fix: drop and count; one Warn.

### C-187 Symbol parsing from columns (S3)
- Fix: from filename before first underscore.

### C-188 UI freeze on 100k+ grid rows (S2)
- Fix: MaxGridRows cap; AddRange with a single Reset.

### C-189 P/Invoke mistakes (S1)
- Detect: wrong struct layout, missing SetLastError, wrong CharSet, IntPtr truncation, handles not freed.
- Fix: SafeHandle, LibraryImport/DllImport with explicit marshalling, check return values.

### C-190 SendInput/PrintWindow assumptions (S2)
- Detect: assumes DPI-aware coordinates; ignores failure return; black frames.
- Fix: DPI conversion, retry/diagnose, log failures (project forbids DXGI path).

### C-191 OCR worker lifecycle (S2)
- Detect: worker processes orphaned; models loaded per call.
- Fix: long-lived worker, kill on exit, batching.

### C-192 Job Object edge cases (S2)
- Detect: assigning after process already spawned children; non-Windows path throwing.
- Fix: check return; no-op on non-Windows.

### C-193 Resource limits not enforced (S2)
- Detect: worker memory cap value not applied; cap set after allocation.
- Fix: attach job immediately after spawn; verify via test job.

### C-194 NVML/DXGI probing cost and failure (S3)
- Detect: probing every second; exceptions on non-NVIDIA.
- Fix: probe once, cache failure.

### C-195 Performance monitor overhead (S3)
- Detect: sampler allocating per tick, string formatting heavy, PerfChip rebuilds entire collection.
- Fix: reuse objects, update in place.

---

# PART H — LOGGING, CONFIG, SECURITY

### C-196 Logging in hot paths (S2)
- Detect: LogManager.Debug per bar/iteration/chunk; per-version Info.
- Fix: aggregate; LogOnce; summary lines.

### C-197 String formatting before level check (S3)
- Detect: interpolated strings built even when discarded (no filtering exists, so all lines cost).
- Fix: avoid building heavy strings in hot loops; summarize.

### C-198 Logging exceptions without the exception (S3)
- Detect: `Error(tag, ex.Message)` without ex.
- Fix: pass the Exception object.

### C-199 Wrong tags/levels (S4)
- Detect: new LogTag values added; Debug for main flow.
- Fix: use existing tags; follow level semantics.

### C-200 Log flood in UI (S2)
- Detect: per-line dispatcher posts; unbounded buffer.
- Fix: bounded queue, coalesced flush, trimmed tail.

### C-201 Logging secrets or personal paths (S2)
- Fix: redact.

### C-202 Settings auto-render bypass (S3)
- Detect: hardcoded XAML controls for AppSettings fields.
- Fix: declare property; reflection renders it.

### C-203 Unsupported setting types (S3)
- Detect: AppSettings property types beyond int/double/bool/string(Color).
- Fix: use supported types or exclude explicitly.

### C-204 Defaults missing for new settings (S2)
- Detect: pre-existing settings.json lacking new keys → zero/null values (e.g., colors empty).
- Fix: defaults in ConfigManager.LoadConfig and normalizers.

### C-205 Settings saved from preview (S3)
- Detect: theme/preview edits persisted without Save.
- Fix: persist only via Save Settings.

### C-206 Concurrent settings writes (S2)
- Detect: SaveConfig from multiple threads; no lock; non-atomic write.
- Fix: lock + atomic write.

### C-207 Secrets in settings.json/git (S1)
- Detect: keys in config; .env committed; .gitignore missing config/data_automation.json, data/ticks.
- Fix: .env only; verify .gitignore.

### C-208 Path traversal (S1)
- Detect: user-provided names (preset names, symbols, strategy names) concatenated into paths.
- Fix: Sanitize; Path.GetFileName; verify resolved path stays under root.

### C-209 Deserializing untrusted JSON into polymorphic types (S2)
- Detect: TypeNameHandling-style settings (Newtonsoft) or custom converters.
- Fix: System.Text.Json source-gen only; no polymorphic type names from files.

### C-210 Executing downloaded or user content (S1)
- Detect: Roslyn scripting of files outside data/strategies; running arbitrary paths.
- Fix: restrict to catalog directory; note trust model in docs.

### C-211 Localhost-only assumption (S2)
- Detect: TcpListener bound to IPAddress.Any.
- Fix: Loopback only.

### C-212 Process-start with shell interpolation (S1)
- Detect: `Process.Start("cmd", "/c " + userString)`.
- Fix: ArgumentList; no shell.

### C-213 Unsafe code unnecessary (S3)
- Detect: AllowUnsafeBlocks enabled broadly; `unsafe` in non-shader code.
- Fix: restrict to what generators require.

### C-214 Package supply chain (S2)
- Detect: new packages added without NuGet verification; preview versions; unmaintained packages.
- Fix: verify on public NuGet; pin versions; justify each addition (rule).

### C-215 Version skew between Avalonia packages (S2)
- Detect: Avalonia core vs DataGrid vs Themes versions diverging unintentionally; ScottPlot.Avalonia compatibility.
- Fix: align; record the compat matrix.

---

# PART I — BUILD, CSPROJ, TOOLING

### C-216 Compile Remove for data folder lost (S1)
- Symptom: strategies compile into the assembly; duplicate top-level statement errors (CS8802).
- Detect: open backs2.csproj, look for `Compile Remove="data\**\*.cs"`.
- Fix: restore the line; rebuild.

### C-217 Stale obj/bin causing phantom errors (S3)
- Symptom: AXSG0111, phantom CS1061 on generated members.
- Detect: errors referencing generated code immediately after refactors.
- Fix: delete obj/ and bin/, `dotnet build --no-incremental`, before changing code.

### C-218 Binding errors treated as root cause (S3)
- Symptom: agent "fixes" XAML bindings while a real CS error exists.
- Detect: build log containing both CS#### and "Compiled binding path ... invalid".
- Fix: fix CS errors first, then re-evaluate.

### C-219 Package added without verification (S2)
- Symptom: restore failure, hallucinated package name or version.
- Detect: PackageReference entries not resolvable on nuget.org.
- Fix: verify id and version exist; remove invented packages.

### C-220 Version floating / wildcard references (S3)
- Detect: `Version="*"` or `Version="1.*"` in PackageReference.
- Fix: pin exact versions for reproducible builds.

### C-221 Analyzer warnings ignored (S3)
- Detect: build output with many warnings; `<NoWarn>` growing.
- Fix: run `dotnet build --no-incremental -p:RunAnalyzersDuringBuild=true`; triage warnings.

### C-222 Nullable annotations disabled or suppressed with `!` (S3)
- Detect: `!` null-forgiving operator overused; `#nullable disable`.
- Fix: model nullability properly; fix root causes.

### C-223 Warnings-as-errors toggled off to "make it build" (S2)
- Detect: csproj TreatWarningsAsErrors flipped, or pragmas added.
- Fix: revert and fix the warning.

### C-224 TargetFramework drift (S3)
- Detect: docs say net8.0 while csproj says otherwise; output path in docs wrong.
- Fix: align docs and csproj.

### C-225 AllowUnsafeBlocks scope creep (S3)
- Detect: unsafe code outside shader/generator needs.
- Fix: restrict; document why enabled.

### C-226 Debug-only code in release path (S3)
- Detect: `#if DEBUG` hiding logic needed in release; Debug.Assert as validation.
- Fix: real validation with exceptions/logging.

### C-227 Verification hooks left reachable (S3)
- Detect: Program.cs flags like --mc-verify, --et-verify, --gpu-verify accessible in shipped builds.
- Fix: keep as documented diagnostics; ensure they exit and never touch user data.

### C-228 Program.Main dispatch ordering (S2)
- Detect: worker flags handled after GUI/log initialization.
- Fix: dispatch --worker / --ocr-worker before GUI framework and LogManager.

### C-229 Missing .gitignore entries (S2)
- Detect: data/ticks, logs, config/data_automation.json, .env, bin, obj, data/cache tracked or untracked-noisy.
- Fix: add entries; remove tracked secrets from history if leaked.

### C-230 Large binaries committed (S3)
- Detect: git ls-files with big sizes (cache JSON, tick CSV, ONNX models, screenshots).
- Fix: git-ignore, LFS, or download step.

### C-231 Line-ending churn (S4)
- Detect: whole-file diffs after AI edits; mixed CRLF/LF.
- Fix: .gitattributes; preserve existing endings.

### C-232 BOM and encoding changes (S4)
- Detect: files rewritten as UTF-8 with/without BOM unexpectedly; mojibake in docs (arrows, em dashes).
- Fix: UTF-8 consistently; set-content encoding explicit.

### C-233 Incremental build assumptions (S4)
- Detect: instructions running build then run.
- Fix: single command per the Build Protocol.

### C-234 Docs-only changes triggering rebuild loops (S4)
- Fix: skip builds for docs-only edits.

### C-235 Long path breaches (S3)
- Detect: generated paths > 200 chars under repo root.
- Fix: flatten; shorten names.

---

# PART J — DOCUMENTATION DRIFT AND AGENT-PROCESS FAILURES

### C-236 Docs describe the framework wrongly (S2)
- Symptom: AGENT_RULES says WPF developer; ARCHITECTURE shows Avalonia 12 and WPF mixed (WPFalonia typos, `.xaml` vs `.axaml`).
- Detect: grep docs for `WPF`, `Avalonia`, `.xaml`, `.axaml`; compare with csproj.
- Fix: one framework name, one extension convention, update RULES/ARCHITECTURE together.

### C-237 Roadmap says planned for shipped work (S3)
- Detect: ROADMAP entries marked PLANNED/PENDING whose code exists; archive says unticked = done.
- Fix: move to archive per Completion Protocol.

### C-238 Roadmap contradictions (S3)
- Detect: a phase "COMPLETED" in header but open boxes inside; duplicate phase numbers (two Phase 27/28/29 blocks).
- Fix: reconcile, single source of truth.

### C-239 Unverified performance claims in docs (S3)
- Detect: "faster", "fixed", "~Nx" statements without a measurement reference.
- Fix: mark UNMEASURED or add measured numbers.

### C-240 Doc line counts and file paths stale (S3)
- Detect: paths such as `docs/pack.ps1`, `docs/strats_guide.md`, `docs/TEST-2026-10-28.md` referenced but missing.
- Fix: create or remove the references.

### C-241 Rules contradicting each other (S2)
- Example candidates: RULES says `ActiveTabIndex`/TabControl while ARCHITECTURE says TreeView/SelectedNode; RULES says ScottPlot.WPF while ARCHITECTURE says Avalonia plot controls.
- Detect: compare §8 of ARCHITECTURE with Tab host rules in RULES.
- Fix: update the stale document.

### C-242 Mixed-language or mojibake in docs (S4)
- Fix: normalize encoding.

### C-243 Agent invents APIs (S1)
- Symptom: calls to methods that do not exist (hallucinated ConfigManager properties, IDataProvider members).
- Detect: build errors CS1061/CS0103; verify symbols exist before use.
- Fix: grep for the symbol first; never assume.

### C-244 Agent edits wrong layer (S2)
- Symptom: symbol translation in DataManager; UI logic in core; core referencing GUI types.
- Detect: namespace and using review: backs2.Core must not reference Avalonia.
- Fix: move code to the correct layer.

### C-245 Partial refactors (S2)
- Symptom: old and new code paths both left in place; dead code and duplicate services.
- Detect: unused classes, methods only referenced by themselves; two implementations of one concept.
- Fix: finish the migration or revert; delete dead code.

### C-246 "Placeholder" and TODO shipped as functionality (S2)
- Detect: grep `TODO`, `FIXME`, `placeholder`, `NotImplementedException`, `// stub`.
- Fix: implement, remove from UI, or document honestly.

### C-247 Silent catch-all handlers (S2)
- Detect: `catch (Exception) { }`, `catch { return null; }`.
- Fix: log with LogManager.Error(..., ex); catch specific exceptions.

### C-248 Over-defensive null checks hiding bugs (S3)
- Detect: `?.` and `?? default` swallowing impossible-null conditions.
- Fix: fail fast at boundaries.

### C-249 Magic numbers without source (S3)
- Detect: literals like 400_000, 10_000, 100, 0.0005, 1500 in code without named constants and a comment.
- Fix: named const with the derivation or measurement reference.

### C-250 Copy-paste duplication (S3)
- Detect: five near-identical provider fetch loops; repeated parse code in CSV and tkc paths.
- Fix: shared helper where semantics are identical; keep separate where rules differ.

### C-251 Overlong methods and god classes (S3)
- Detect: files > 600 lines without partial-class split; methods > 150 lines.
- Fix: partial classes per the project convention; extract pure functions.

### C-252 Inconsistent naming (S4)
- Detect: Mixed Async suffix usage, mixed _field/field naming.
- Fix: follow repo convention.

### C-253 Comments narrating the change (S4)
- Detect: comments like "// fixed bug", "// new", "// added for Phase 29".
- Fix: comments explain why, not history; history belongs in commits.

### C-254 Commit hygiene violations (S3)
- Detect: `git add -A` with unrelated files; vague messages; commits on broken builds.
- Fix: explicit paths; detailed bodies per RULES.

### C-255 Skipping verification (S1)
- Symptom: "should work" claims.
- Fix: run the single build/run command; report real output.

### C-256 Fixing symptoms not causes (S2)
- Example: lowering GPU threshold to make the monitor look busy.
- Fix: measure first; document expected readings.

### C-257 Optimizing without baseline (S2)
- Detect: perf changes with no before/after numbers.
- Fix: instrument (Stopwatch/[startup]-style markers), record numbers.

### C-258 Profiler overhead mistaken for result (S3)
- Detect: perf conclusions drawn under instrumentation with 24x overhead.
- Fix: use counters/Stopwatch for measurement; profilers for attribution only.

### C-259 Tests missing for invariants (S2)
- Detect: no tests for metrics degenerate inputs, TimeSliceBuilder, grid builder, window planner, tkc decoder, bucket math.
- Fix: add focused unit tests or --verify hooks.

### C-260 Tests that mock away the bug (S3)
- Detect: tests asserting mocks were called, not outputs.
- Fix: golden-value tests with real data.

### C-261 Non-deterministic tests (S3)
- Detect: unseeded Random, DateTime.Now, parallel ordering assumptions.
- Fix: seeds, injected clocks, sort before compare.

### C-262 No regression harness for numeric changes (S2)
- Detect: refactors of kernels/metrics without element-by-element comparison against archived batch.
- Fix: golden batch comparison (as Phase 31 did).

### C-263 Rounding/format in assertions (S3)
- Fix: tolerance-based compares for doubles; exact compare for integers.

### C-264 Feature flags and settings without UI surfacing (S4)
- Fix: AppSettings auto-render or documented hidden setting.

### C-265 Shipping instrumentation that costs (S3)
- Detect: [29A]/[30]/[33] stage timers active in production loops.
- Fix: gate by setting or keep cost negligible; verify overhead.

---

# PART K — FAMILY-SPECIFIC TRAPS (project-shaped)

### C-266 Optimizer parallel backtests share TradeRecorder or globals (S1)
- Detect: shared instances across workers.
- Fix: per-version instances.

### C-267 ThrottledProgress dropping the final report (S2)
- Detect: terminal 100% never shown.
- Fix: terminal flush guaranteed.

### C-268 LogManager ring buffer thread-safety (S2)
- Detect: List<LogEntry> mutated without lock; GetRecentEntries copying under contention.
- Fix: lock or lock-free ring; snapshot copy.

### C-269 LogManager.Logged raised on arbitrary thread to UI (S2)
- Detect: handlers touching UI/collections directly.
- Fix: marshal in subscriber.

### C-270 Serilog flush on crash (S3)
- Detect: Log.CloseAndFlush absent in crash path.
- Fix: flush in finally of Main.

### C-271 FileSystemWatcher debounce and error buffer overflow (S3)
- Detect: StrategyCatalogService without InternalBufferSize handling or Error event.
- Fix: handle Error, rescan.

### C-272 FileSystemWatcher raising on partial writes (S3)
- Detect: reading a strategy while the editor is mid-save.
- Fix: retry on IOException with short delay.

### C-273 Preset name sanitation collisions (S3)
- Detect: names differing only by `_` vs `-` overwrite each other.
- Fix: detect collisions; confirm overwrite.

### C-274 Account JSON schema drift (S2)
- Detect: new fields without nullable handling; null phases causing NRE in MonteCarloViewModel.
- Fix: nullable defaults; validation on load with Warn.

### C-275 Active account triple stale (S2)
- Detect: settings reference firm/plan/size that no longer exists.
- Fix: validate at load; fall back with Warn.

### C-276 AccountService async init race (S2)
- Detect: MonteCarlo reading AccountService.Active before init completes.
- Fix: guard uninitialized state; await readiness.

### C-277 TimeSliceBuilder day-boundary logic (S2)
- Detect: EOD flag by day-of-year; timezone not applied.
- Fix: compare date; use account/market timezone if rules require.

### C-278 TimeSliceBuilder with unsorted or duplicated bars (S1)
- Fix: validate input order; clamp indices.

### C-279 Trade indices out of range (S1)
- Detect: EntryIndex/ExitIndex >= Data.Count; Exit before Entry.
- Fix: bounds validation.

### C-280 MonteCarlo cluster click uses stale plot coordinates (S3)
- Detect: GetCoordinates with wrong scaling factor after DPI change.
- Fix: use current RenderScaling.

### C-281 K-means on degenerate features (S3)
- Detect: zero variance → NaN in Z-score.
- Fix: guard std=0; k ≤ N.

### C-282 NavigateToTradeMessage with unqueued dataset (S3)
- Fix: warn only, never mutate queue (documented).

### C-283 Data tab group-header checkbox state loops (S3)
- Detect: toggling header triggers per-row handlers that retrigger header sync.
- Fix: suppression flag during bulk updates.

### C-284 Queue persistence restores "Unknown" provider (S4)
- Fix: lazily derive from cache metadata.

### C-285 Providers tab re-entrancy (S3)
- Fix: `_refreshing` guard.

### C-286 StreamingLoader cancellation latest-wins bugs (S2)
- Detect: older load continuing to post batches after a newer started; sink invoked after dispose.
- Fix: version token checked before each post.

### C-287 StreamingLoader batch posts starving input (S3)
- Detect: Dispatcher priority too high for batches.
- Fix: Background/Render priority; small batches.

### C-288 Walk-forward persist flag leakage (S2)
- Detect: IS/OOS optimization runs persisting batch files (persist=false must hold).
- Fix: verify the flag is honored in every path.

### C-289 Walk-forward carry-top-N misuse (S2)
- Detect: carrying parameters chosen on OOS windows into subsequent IS windows.
- Fix: strict chronological dependency.

### C-290 Compute worker stdout pollution (S2)
- Detect: Console.Out used for diagnostics while pipes carry protocol.
- Fix: stderr for diagnostics only.

---

# APPENDIX 1 — GREP / POWERSHELL CHEAT SHEET

All commands are read-only. Run from repo root.

- Find LINQ in core: `Select-String -Path src\core\*\*.cs -Pattern '\.(Select|Where|OrderBy|ToList|ToArray|Any|Sum|Count)\('`
- Find async void: `Select-String -Path src\**\*.cs -Pattern 'async void'`
- Find blocking waits: `Select-String -Path src\**\*.cs -Pattern '\.Result\b|\.Wait\(|GetAwaiter\(\)\.GetResult'`
- Find Thread.Sleep: `Select-String -Path src\**\*.cs -Pattern 'Thread\.Sleep'`
- Find fire-and-forget: `Select-String -Path src\**\*.cs -Pattern '^\s*_\s*=\s*\w+.*Async\('`
- Find empty catch: `Select-String -Path src\**\*.cs -Pattern 'catch\s*(\(.*\))?\s*\{\s*\}'`
- Find static fields: `Select-String -Path src\**\*.cs -Pattern '\bstatic\b.*(Dictionary|List|HashSet|Random)'`
- Find new HttpClient: `Select-String -Path src\**\*.cs -Pattern 'new HttpClient'`
- Find DateTime.Now: `Select-String -Path src\**\*.cs -Pattern 'DateTime\.Now|ToLocalTime'`
- Find culture-sensitive parse: `Select-String -Path src\**\*.cs -Pattern '(double|int|decimal)\.Parse\('`
- Find ToString on doubles: `Select-String -Path src\**\*.cs -Pattern '\.ToString\("[FNGE0-9]'`
- Find Regex construction: `Select-String -Path src\**\*.cs -Pattern 'new Regex\('`
- Find string.Format: `Select-String -Path src\**\*.cs -Pattern 'string\.Format\('`
- Find Dispatcher posts: `Select-String -Path src\**\*.cs -Pattern 'Dispatcher\.UIThread\.(Post|InvokeAsync)'`
- Find event subscriptions: `Select-String -Path src\**\*.cs -Pattern '\w+\.(Changed|Logged|ActiveChanged|CountProgress)\s*\+='`
- Find unsubscriptions: `Select-String -Path src\**\*.cs -Pattern '\w+\.(Changed|Logged|ActiveChanged|CountProgress)\s*-='`
- Find View resolving VM: `Select-String -Path src\gui\**\*.axaml.cs -Pattern 'GetRequiredService'`
- Find hardcoded hex in XAML: `Select-String -Path src\gui\**\*.axaml -Pattern '#[0-9A-Fa-f]{6,8}'`
- Find $parent bindings: `Select-String -Path src\gui\**\*.axaml -Pattern '\$parent\['`
- Find ColorPicker use: `Select-String -Path src\gui\**\*.axaml -Pattern 'ColorPicker|ColorView'`
- Find WindowState Maximized: `Select-String -Path src\gui\**\*.axaml -Pattern 'WindowState="Maximized"'`
- Find TODO markers: `Select-String -Path src\**\*.cs -Pattern 'TODO|FIXME|HACK|NotImplementedException'`
- Find placeholder text: `Select-String -Path src\**\*.cs -Pattern '(?i)placeholder|stub'`
- Find unsafe: `Select-String -Path src\**\*.cs -Pattern '\bunsafe\b'`
- Find lock usage: `Select-String -Path src\**\*.cs -Pattern '\block\s*\('`
- Find Parallel usage: `Select-String -Path src\**\*.cs -Pattern 'Parallel\.(For|ForEach|ForEachAsync)'`
- Find Task.Run: `Select-String -Path src\**\*.cs -Pattern 'Task\.Run'`
- Find CancellationTokenSource: `Select-String -Path src\**\*.cs -Pattern 'new CancellationTokenSource'`
- Find Process.Start: `Select-String -Path src\**\*.cs -Pattern 'Process\.Start|ProcessStartInfo'`
- Find File sync IO: `Select-String -Path src\**\*.cs -Pattern 'File\.(ReadAll|WriteAll)(Text|Bytes|Lines)\('`
- Find Directory enumeration: `Select-String -Path src\**\*.cs -Pattern 'Directory\.(GetFiles|EnumerateFiles)'`
- Find JsonSerializer w/o context: `Select-String -Path src\**\*.cs -Pattern 'JsonSerializer\.(Serialize|Deserialize)'`
- Find API key in URLs logged: `Select-String -Path src\core\**\*.cs -Pattern 'apikey|access_key|token='`
- Find Compile Remove: `Select-String -Path backs2.csproj -Pattern 'Compile Remove'`
- Find package versions: `Select-String -Path backs2.csproj -Pattern 'PackageReference'`
- Find gitignore entries: `Get-Content .gitignore`
- Find tracked big files: `git ls-files | ForEach-Object { Get-Item $_ } | Sort-Object Length -Descending | Select-Object -First 20 FullName, Length`
- File sizes by lines: `Get-ChildItem src -Recurse -Filter *.cs | ForEach-Object { [pscustomobject]@{ Lines=(Get-Content $_.FullName).Count; Path=$_.FullName } } | Sort-Object Lines -Descending | Select-Object -First 25`
- Count TODOs per file: `Select-String -Path src\**\*.cs -Pattern 'TODO' | Group-Object Path | Sort-Object Count -Descending`
- Last build errors: `dotnet build --no-incremental 2>&1 | Select-String 'error|warning CS' | Select-Object -First 50`
- Doc references to missing files: `Select-String -Path docs\*.md -Pattern 'docs[\\/][\w\-\.]+' -AllMatches | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique`

---

# APPENDIX 2 — AUDIT CHECKLISTS BY AREA

Use these as prompts: each line is a question the auditor must answer with evidence (file:line) or "not applicable".

## Checklist: ViewModels
- Does every ViewModel inherit ObservableObject and is it `public partial`?
- Does any constructor touch disk, network, or run loops?
- Are all event subscriptions unsubscribed or weak?
- Are all async commands guarded against re-entry?
- Are all long operations cancellable and do they dispose their CTS?
- Do all collection mutations occur on the UI thread?
- Are PropertyChanged notifications raised for computed properties?
- Are CanExecute dependencies notified?
- Is there any `Application.Current` usage that belongs in the View?
- Are there any ViewModel references to View types?
- Do status strings get set at high frequency?
- Are DataGrid sources replaced or mutated in batches?
- Is any file dialog opened from the ViewModel without an abstraction?
- Are exceptions surfaced to a status line and logged?
- Does each refresh path have a `_refreshing` guard?

## Checklist: Views and XAML
- Does each view set x:DataType?
- Do DataTemplates use the approved RelativeSource syntax?
- Are colors, spacing, fonts token-based?
- Do code-behinds contain only InitializeComponent and view-local wiring?
- Is DataContext-dependent wiring in OnDataContextChanged?
- Are plots focusable with input processors disabled?
- Are popups and ComboBoxes using valid styles for the framework version?
- Are DataGrid header widths safe for sort glyphs?
- Are collapsed Expanders avoiding heavy content loading?
- Are any controls built in code in loops?

## Checklist: Core data layer
- Is cache write atomic?
- Does LoadCacheAsync detect all legacy formats?
- Is memory-cache usage bounded?
- Are provider results validated (sorted, unique, OHLC sane)?
- Does fallback log reasons?
- Is the provider symbol mapping inside providers only?
- Is timeframe mapping inside TimeframeMap only?
- Are keys normalized?
- Are HTTP calls through the resilient named client?
- Are cancellation tokens passed to HTTP calls?
- Are 429/5xx handled by the resilience pipeline?
- Is the origin discriminator preserved on save?
- Can a poorer fetch overwrite richer cached data?
- Are cache metadata memo keys correct?
- Are partial fetches never cached as complete?

## Checklist: Strategy runtime
- Is Compile Remove present for data folder?
- Is compile single-flight?
- Does a failing strategy produce an error result instead of aborting the batch?
- Are Params invariant-culture strings?
- Is parameter parsing tolerant and warns on misplaced @param?
- Are strategies guarded against infinite loops?
- Are lookahead patterns present in shipped strategies?
- Are metrics called with whole series inside loops?
- Is TradeRecorder validated for invalid sequences?
- Are trade timestamps from Data[index]?
- Is globals construction cheap?

## Checklist: Metrics
- Does each metric return a defined value for empty and single-bar inputs?
- Is any metric stateful?
- Do any metrics allocate per call beyond need?
- Are registry names unique and documented?
- Does a miss log once at Debug?
- Are RSI/ATR values validated against reference implementation?
- Does volume profile handle zero range and zero volume?

## Checklist: Monte Carlo and optimizer
- Does the kernel follow MTM-low → realize → HWM → EOD order?
- Are all three drawdown types implemented distinctly?
- Is nominal (non-percent) compounding used?
- Is RNG seeded by absolute iteration index?
- Are caps enforced in VM and engine?
- Are cancelled results marked partial?
- Are curves downsampled and capped?
- Is progress throttled?
- Does GPU dispatch gate on workload size?
- Is the GPU first-dispatch race gate intact?
- Does the batch path use the same BuildResult?
- Are walk-forward windows strictly partitioned?
- Are result files bounded or indexed?
- Does Edge % require minimum trades?
- Are parameter grids index-based and overflow-safe?

## Checklist: MT5 and native
- Is the inclusive-boundary rule mirrored on both sides?
- Are timeouts span-scaled per action?
- Is the immediate ack present for SYNC?
- Is PROGRESS parsing lenient?
- Is backpressure preserved?
- Does the TCP listener bind to loopback only?
- Are pending requests failed on disconnect?
- Are TCS options async-continuation safe?
- Are decoder rules from ARCHITECTURE §20 intact?
- Is decoding serial across files?
- Is logging limited to one Info per file?
- Are P/Invoke signatures correct and handles safe?
- Is the worker killed on cancel and exit?
- Is the Job Object memory limit actually applied?

## Checklist: Logging and config
- Is LogManager the only static?
- Are exceptions logged with the exception object?
- Are secrets redacted?
- Do hot paths avoid per-item logs?
- Does the UI log view coalesce?
- Are new AppSettings properties defaulted for old files?
- Is SaveConfig atomic and serialized?
- Are theme values validated before parsing?
- Are .env values normalized?
- Is settings.json free of secrets?

## Checklist: Build and repo
- Does the build pass with analyzers?
- Are there new warnings versus baseline?
- Are packages verified and pinned?
- Is .gitignore complete?
- Are docs consistent with code (framework, tab host, paths)?
- Are referenced docs present?
- Is the working tree clean after changes?
- Do commit messages follow the convention?

---

# APPENDIX 3 — SEVERITY TRIAGE RULES FOR realissues.md

1. Confirmed S1 items are fixed first, one at a time, each verified.
2. S2 items next, grouped by file to minimize churn.
3. S3/S4 items only when the fix is trivial and safe, or when they block clarity of S1/S2 fixes.
4. Never "fix" an item that cannot be confirmed in code; record it as REJECTED with the reason.
5. Each fix must name the verification used: build output, a verification hook, or a measured number.
6. Performance fixes require a before and after measurement or are marked UNMEASURED.
7. Do not change documented invariants (determinism contract, inclusive-boundary rule, tkc decoder rules) without proving the current behavior wrong.
8. Docs must be updated in the same change when behavior or rules change.

---

# APPENDIX 4 — realissues.md ENTRY TEMPLATE

- ID: R-001 (maps to commons id, e.g. C-043)
- Status: CONFIRMED | REJECTED | FIXED | DEFERRED
- Severity: S1–S4
- Location: path:line-range
- Evidence: exact code excerpt or measurement
- Impact: what breaks or how slow
- Fix plan: minimal change
- Verification: command and result
- Notes: caveats, follow-ups

---

# APPENDIX 5 — ANTI-PATTERNS FOR THE FIXING PHASE

- Do not rewrite whole files when a targeted change is enough.
- Do not introduce new packages to fix perf.
- Do not add static globals to share state.
- Do not add LINQ or reflection to hot paths.
- Do not catch and ignore exceptions to make tests pass.
- Do not disable analyzers or warnings.
- Do not lower thresholds or caps to make diagnostics look better.
- Do not reformat unrelated code.
- Do not change line endings or encodings of untouched files.
- Do not commit with a failing build.
- Do not leave temporary diagnostic code behind.
- Do not claim a fix without running the build or verification hook.

---

# APPENDIX 6 — QUICK SYMPTOM → SUSPECT MAP

- UI freeze on tab open → C-045, C-046, C-048, C-286
- UI stutter during Optimize → C-047, C-166, C-200, C-049
- Memory grows after switching tabs → C-061, C-060, C-062, C-100
- Working set above 2 GB → C-009, C-010, C-165, C-182
- CPU below 30% under parallel load → C-024, C-087, C-023, C-025
- GPU idle during optimize → C-031 (expected), C-032
- Probabilities do not sum to one → C-158, C-159
- Best parameters look too good → C-140, C-162, C-163, C-169
- NaN in results → C-120, C-142, C-281
- Missing bars at page seams → C-103, C-104, C-171
- Wrong bar open/close from ticks → C-184, C-185
- Daily bars misaligned → C-105, C-184
- Strategy compile errors on build → C-216, C-126
- Phantom build errors → C-217, C-218, C-055
- Settings missing after upgrade → C-204
- Crash on startup before log → C-228, C-270
- Zombie compute processes → C-089, C-090, C-192
- SYNC timeouts on big ranges → C-174, C-175
- Cache corrupted after crash → C-043, C-121
- Free provider overwrote good data → C-122

---

# APPENDIX 7 — ONE-LINE REMINDERS FOR AGENTS

- Read the docs before editing; they are the contract.
- Grep for a symbol before using it.
- Keep hot paths allocation-free.
- Keep parallelism single-owner.
- Keep UI updates batched and on the UI thread.
- Keep I/O async and bounded.
- Keep secrets out of code, logs, and config.
- Keep invariants documented and tested.
- Keep fixes small and verified.
- Keep docs in sync with code.
- Prefer measurement over intuition.
- Prefer deletion of dead code over wrapping it.
- Prefer explicit errors over silent fallbacks.
- Prefer invariant culture in data paths.
- Prefer UTC and Unix milliseconds in core logic.
- Prefer immutable snapshots across threads.
- Prefer source generators over reflection.
- Prefer token resources over literal colors.
- Prefer partial classes over mega-files.
- Prefer one build or one run command per change, never both.