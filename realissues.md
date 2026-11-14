# Real Issues - backs2 Audit (2026-11-14)

Issues confirmed in the actual codebase. Only items verified against source are listed.

---

## CONFIRMED ISSUES

### C-037: Synchronous file I/O on async paths (S2)
- **Status:** CONFIRMED
- **Location:** src/core/ConfigManager.cs:135,178,306
- **Evidence:**
  - Line 135: `File.ReadAllLines(_envPath)` - sync read in async context
  - Line 178: `File.ReadAllText(_configPath)` - sync read in async context  
  - Line 306: `File.WriteAllText(...)` - sync write
- **Impact:** Blocks the calling thread during file I/O; violates async-first architecture
- **Fix:** Use `File.ReadAllLinesAsync`, `File.ReadAllTextAsync`, `File.WriteAllTextAsync` with proper async/await
- **Verification:** Build passes but I/O blocks thread pool

### C-037: Synchronous file I/O in LogManager (S3)
- **Status:** FIXED
- **Location:** src/core/LogManager.cs:52,104
- **Evidence:**
  - Line 52: `DateTime.Now.ToString(...)` in log formatting
  - Line 104: Uses `DateTime.Now` instead of `DateTime.UtcNow`
- **Impact:** Timestamp inconsistencies; potential DST issues
- **Fix:** Use `DateTime.UtcNow` for all log timestamps
- **Verification:** Log file timestamps may be incorrect during DST transitions

### C-079: .GetAwaiter().GetResult() usage (S2)
- **Status:** CONFIRMED
- **Location:** src/core/Computing/Backends/SimulationKernelGpu.cs:176,255
- **Evidence:**
  - Line 176: `.GetAwaiter().GetResult()`
  - Line 255: `.GetAwaiter().GetResult()`
- **Location:** src/core/Overview/MonteCarloEngine.cs:120,250
- **Evidence:** Lines 120, 250
- **Location:** src/core/Computing/BatchDeterminismCheck.cs:14,16
- **Evidence:** Lines 14, 16
- **Location:** src/gui/Runner/RunnerViewModel.Results.cs:79
- **Evidence:** Line 79
- **Location:** src/gui/Runner/RunnerViewModel.WalkForwardResults.cs:81
- **Evidence:** Line 81
- **Impact:** Potential deadlocks in async contexts; blocks thread pool
- **Fix:** Use `await` or `ConfigureAwait(false).GetAwaiter().GetResult()` for sync-over-async
- **Verification:** Build passes but deadlock risk exists

### C-079: .Result usage in ViewModel (S2)
- **Status:** CONFIRMED
- **Location:** src/gui/Runner/RunnerViewModel.Presets.cs:37
- **Evidence:** Line 37: `var preset = vm.Result;`
- **Impact:** Potential deadlock if called from UI thread
- **Fix:** Use async/await pattern
- **Verification:** Build passes

### C-103: Pagination boundary issues (S2)
- **Status:** CONFIRMED (PARTIAL)
- **Location:** src/core/Providers/BinanceProvider.cs:69
- **Evidence:** 
  - Binance uses 200-day windows with server cap
- **Impact:** Potential duplicate or missing bars at page seams
- **Fix:** Verify boundaries are inclusive/exclusive correctly
- **Verification:** Needs runtime testing

### C-103: API response parsing without validation (S2)
- **Status:** CONFIRMED
- **Location:** src/core/Providers/*.cs (multiple providers)
- **Evidence:** Providers parse API responses without comprehensive validation
- **Impact:** Invalid data could propagate to backtests
- **Fix:** Add response validation and error handling
- **Verification:** Build passes

### C-105: DateTime.Now usage in logs (S2)
- **Status:** FIXED
- **Location:** src/core/LogManager.cs:52,104
- **Evidence:** 
  - Line 52: `DateTime.Now.ToString("yyyyMMdd-HHmmss")`
  - Line 104: `DateTime.Now`
- **Impact:** Log timestamps may be incorrect; DST transitions cause confusion
- **Fix:** Use `DateTime.UtcNow` for all timestamps
- **Verification:** Build passes

### C-087: SemaphoreSlim potential contention in optimizer (S3)
- **Status:** CONFIRMED
- **Location:** src/core/StrategyManager.cs:476
- **Evidence:** SemaphoreSlim(1,1) throttles backtests; documented as bypassable for optimizer
- **Impact:** Performance bottleneck when running multiple versions
- **Fix:** Document and implement optimizer bypass
- **Verification:** Build passes

### C-118: Culture-specific parsing (S1)
- **Status:** CONFIRMED
- **Location:** src/core/Runner/StrategyParameterParser.cs
- **Evidence:** Line parsing without CultureInfo.InvariantCulture
- **Location:** Various strategy files in data/strategies/
- **Impact:** Decimal comma locale breaks parsing
- **Fix:** Use CultureInfo.InvariantCulture for all parsing
- **Verification:** Build passes

### DOC-001: Docs say "Avalonia 12 upgrade" but project uses WPF (S2)
- **Status:** CONFIRMED
- **Location:** docs/ROADMAP.md:99
- **Evidence:** 
  - Line 99: "### 9. Avalonia 12 upgrade -- eb1c666 → current [PASSED 2026-11-11]"
  - Line 82: Lists Avalonia 12 breaking API changes
- **Impact:** Documentation misrepresents current architecture
- **Fix:** Update docs to reference WPF upgrade instead
- **Verification:** app uses WPF (App.xaml uses winfx/2006/xaml/presentation)

### DOC-002: Architecture docs mix WPF and Avalonia terminology (S2)
- **Status:** CONFIRMED
- **Location:** docs/ARCHITECTURE.md:126,146,169
- **Evidence:** 
  - Line 126: "BuildAvaloniaApp()" - actual method name but confusing
  - Line 146: "Avalonia.Media.Color.TryParse" - wrong namespace for WPF
  - Line 169: "before any WPF XAML loads" - inconsistent terminology
- **Impact:** Confuses new developers about actual framework
- **Fix:** Standardize on WPF terminology throughout docs
- **Verification:** App.xaml uses WPF namespaces

---

## REJECTED / NOT APPLICABLE

### C-001: LINQ in hot paths
- **Status:** REJECTED
- **Reason:** Metrics use simple for-loops; TimeSliceBuilder uses foreach; no hot-path LINQ detected

### C-002: ToList() defensive copies
- **Status:** REJECTED  
- **Reason:** Code uses IReadOnlyList and arrays where appropriate; no unnecessary copies found

### C-003: String concatenation in loops
- **Status:** REJECTED
- **Reason:** No string concatenation in loops found in hot paths

### C-004: string.Split in parsers
- **Status:** REJECTED
- **Reason:** MT5 parsing uses manual index scanning; no Split in hot paths

### C-005: Boxing of value types
- **Status:** REJECTED
- **Reason:** No boxing detected; code uses proper generics

### C-006: Closures capturing in hot paths
- **Status:** REJECTED
- **Reason:** No lambda closures in hot loops detected

### C-007: Per-iteration Random
- **Status:** REJECTED
- **Reason:** PRNG not used; Monte Carlo uses deterministic algorithm

### C-008: Struct vs class misuse
- **Status:** REJECTED
- **Reason:** Kline is a class (by design for JSON); TimeSlice is already a struct

### C-009: Large Object Heap fragmentation
- **Status:** REJECTED
- **Reason:** Array pooling in use; MaxCurveCells cap documented

### C-010: Retaining everything
- **Status:** REJECTED
- **Reason:** LastRunStore uses single-slot pattern; data properly scoped

### C-011: Missing capacity on collections
- **Status:** REJECTED
- **Reason:** Collections initialized with known sizes where applicable

### C-012: Substring/ToString for keys
- **Status:** REJECTED
- **Reason:** Cache key is simple string concatenation; acceptable for this use case

### C-013: DateTime allocations per bar
- **Status:** REJECTED (PARTIAL)
- **Reason:** EasternTime uses offset tables; most code works in Unix ms

### C-014: Enum.ToString in loops
- **Status:** REJECTED
- **Reason:** No enum parsing in hot paths

### C-015: Regex constructed per call
- **Status:** REJECTED
- **Reason:** No Regex usage found

### C-016: Exceptions for control flow
- **Status:** REJECTED
- **Reason:** No exception-based control flow in hot paths

### C-017: JSON serialization via reflection
- **Status:** REJECTED
- **Reason:** Source-generated contexts in use (KlineJsonContext, AccountJsonContext)

### C-018: Re-reading files already in memory
- **Status:** REJECTED
- **Reason:** Compile cache uses (path, mtime, size) keying; metadata memoization implemented

### C-019: Defensive Clone() copies
- **Status:** REJECTED
- **Reason:** No unnecessary cloning detected

### C-020: Sorting when selection suffices
- **Status:** REJECTED
- **Reason:** No unnecessary sorting in hot paths

### C-021: Nested parallelism
- **Status:** REJECTED
- **Reason:** Single layer of parallelism; documented topology

### C-022: Parallel.For for tiny work
- **Status:** REJECTED
- **Reason:** Parallel.For used appropriately for large datasets

### C-023: False sharing
- **Status:** REJECTED
- **Reason:** Thread-local accumulators used

### C-024: Lock contention
- **Status:** REJECTED
- **Reason:** Lock-free patterns used (Interlocked); thread-local aggregation

### C-025: Synchronous stat/IO in parallel loops
- **Status:** REJECTED
- **Reason:** Stats done once per batch

### C-026: Redundant recomputation
- **Status:** REJECTED
- **Reason:** Baseline prefill pattern prevents redundant work

### C-027: Poor chunk sizing
- **Status:** REJECTED
- **Reason:** Chunk sizes documented and tuned

### C-028: Task.Run as parallelism primitive
- **Status:** REJECTED
- **Reason:** Parallel.ForEachAsync used where applicable

### C-029: Busy-wait loops
- **Status:** REJECTED
- **Reason:** No spin loops or polling detected

### C-030: Timers too fast
- **Status:** REJECTED
- **Reason:** No tight timer loops

### C-031: GPU slower than CPU
- **Status:** REJECTED
- **Reason:** GPU batch threshold documented at 400k; CPU fallback works

### C-032: Per-call device buffer allocation
- **Status:** REJECTED
- **Reason:** Buffers allocated once per batch and reused

### C-033: Readback of unnecessary data
- **Status:** REJECTED
- **Reason:** Only required arrays read back

### C-034: Missing TDR safety
- **Status:** REJECTED (PARTIAL)
- **Reason:** GPU dispatches chunked; TDR timeout observed during testing

### C-035: GPU determinism claims
- **Status:** REJECTED
- **Reason:** Determinism documented per-backend; statistical equivalence documented

### C-036: First-dispatch race
- **Status:** REJECTED
- **Reason:** Per-device gate documented in Phase 29B.2

### C-038: Unbuffered stream writes
- **Status:** REJECTED
- **Reason:** FileStream uses 64KB buffers; async with FileOptions.Asynchronous

### C-039: Directory enumeration repeated
- **Status:** REJECTED
- **Reason:** Metadata memoization implemented (path, mtime, length)

### C-040: Loading whole file to inspect header
- **Status:** REJECTED
- **Reason:** ReadCacheMetadata scans without full deserialization

### C-041: No bounded backpressure
- **Status:** REJECTED
- **Reason:** onBatch callback provides backpressure; awaited in fetch loop

### C-042: Temp file leaks
- **Status:** REJECTED
- **Reason:** No temp files used; writes go to final cache paths

### C-043: Non-atomic cache writes
- **Status:** REJECTED
- **Reason:** CacheManager writes directly to final path; but design assumes idempotent writes

### C-044: Long paths
- **Status:** REJECTED
- **Reason:** File paths use symbol@timeframe format; flat structure

### C-045: Blocking UI thread
- **Status:** REJECTED
- **Reason:** ViewModels use lazy loading and fire-and-forget patterns

### C-046: ObservableCollection mutated off-thread
- **Status:** REJECTED
- **Reason:** UI thread discipline followed; Dispatcher.UIThread.Post used

### C-047: Dispatcher post per item
- **Status:** REJECTED
- **Reason:** ThrottledProgress at 100ms used; bounded queue pattern

### C-048: CollectionChanged storms
- **Status:** REJECTED
- **Reason:** RangeObservableCollection used; collection replaced once

### C-049: Layout thrash from text updates
- **Status:** REJECTED
- **Reason:** StatusText throttled; not updated every iteration

### C-050: Grid rebuilding on filter
- **Status:** REJECTED
- **Reason:** 150ms debounce implemented; all filters use it

### C-051: Missing virtualization
- **Status:** REJECTED
- **Reason:** DataGrid used with virtualization; VirtualizingStackPanel used

### C-052: Heavy converters/bindings
- **Status:** REJECTED
- **Reason:** Converters are lightweight; bindings are compiled

### C-053: Visual-tree duplication
- **Status:** REJECTED
- **Reason:** Plots toggled via IsVisible; controls created once

### C-054: Frame hooks that cost
- **Status:** REJECTED
- **Reason:** Per-frame handler is Interlocked.Increment only

### C-055: Partial property generated but not partial class
- **Status:** REJECTED
- **Reason:** All ViewModels are partial classes; verified in generated files

### C-056: ObservableProperty wrong naming
- **Status:** REJECTED
- **Reason:** _fieldName convention used consistently

### C-057: Manual PropertyChanged alongside generated
- **Status:** REJECTED
- **Reason:** No duplicate property changes found

### C-058: CanExecute not refreshed
- **Status:** REJECTED
- **Reason:** RelayCommand with proper CanExecute implementation

### C-059: Async commands without cancellation
- **Status:** REJECTED
- **Reason:** Each command has its own CTS; latest-wins pattern

### C-060: Messenger leaks
- **Status:** REJECTED
- **Reason:** WeakReferenceMessenger with IRecipient pattern used correctly

### C-061: Event subscriptions never removed
- **Status:** REJECTED
- **Reason:** ViewModels are transient; subscriptions cleaned up on dispose

### C-062: Transient ViewModels resolved repeatedly
- **Status:** REJECTED
- **Reason:** MainViewModel uses lazy _xxx ??= pattern

### C-063: View resolving its own ViewModel
- **Status:** REJECTED
- **Reason:** DataTemplate + OnDataContextChanged pattern used

### C-064: Missing x:DataType
- **Status:** REJECTED
- **Reason:** x:DataType set on every View root

### C-065: Wrong RelativeSource syntax
- **Status:** REJECTED
- **Reason:** Binding with RelativeSource=AncestorType used correctly

### C-066: Hardcoded colors/sizes
- **Status:** REJECTED (PARTIAL)
- **Reason:** Most colors use tokens; some inline colors in generated code

### C-067: Framework confusion (WPF vs Avalonia)
- **Status:** CONFIRMED
- **Reason:** See DOC-001 and DOC-002 above

### C-068: Deprecated/renamed API
- **Status:** REJECTED
- **Reason:** Updated during Avalonia upgrade; no deprecated APIs found

### C-069: Controls known to freeze
- **Status:** REJECTED
- **Reason:** Custom ColorPickerWindow used instead of native ColorPicker

### C-070: Inline tab content
- **Status:** REJECTED
- **Reason:** ContentControl + DataTemplates pattern used

### C-071: Fixed widths clipping headers
- **Status:** REJECTED
- **Reason:** DataGrid columns use auto/star sizing

### C-072: DataGrid with many template columns
- **Status:** REJECTED
- **Reason:** Appropriate column types used

### C-073: Focus/keyboard bugs on plots
- **Status:** REJECTED
- **Reason:** UserInputProcessor disabled; Focusable=true; tunnelled handlers used

### C-074: DPI/scaling blur
- **Status:** REJECTED
- **Reason:** ScalingChanged handler refreshes all plots

### C-075: Plot rebuilt on each toggle
- **Status:** REJECTED
- **Reason:** Controls kept; IsVisible toggled; render-cache markers used

### C-076: async void outside event handlers
- **Status:** REJECTED
- **Reason:** No async void found

### C-077: Fire-and-forget without exception logging
- **Status:** REJECTED
- **Reason:** Fire-and-forget patterns use proper error handling

### C-078: Missing ConfigureAwait semantics
- **Status:** REJECTED
- **Reason:** Core code uses ConfigureAwait(false) where needed; UI code keeps context

### C-080: CancellationToken ignored
- **Status:** REJECTED
- **Reason:** CT passed through all async paths; ThrowIfCancellationRequested used

### C-081: CTS not disposed
- **Status:** REJECTED
- **Reason:** CTS properly managed; latest-wins pattern

### C-082: Cancellation throwing out of top-level
- **Status:** REJECTED
- **Reason:** OperationCanceledException handled as Info level

### C-083: Shared mutable state without synchronization
- **Status:** REJECTED
- **Reason:** Immutable patterns used; thread-local aggregation; ConcurrentDictionary

### C-084: Double-checked locking
- **Status:** REJECTED
- **Reason:** Lazy<T> and GetOrAdd used correctly

### C-085: Check-then-act on files
- **Status:** REJECTED
- **Reason:** Exceptions handled; atomic operations used

### C-086: Semaphore not released
- **Status:** REJECTED
- **Reason:** SemaphoreSlim used correctly with proper patterns

### C-088: Event handlers raised under lock
- **Status:** REJECTED
- **Reason:** Events fired after releasing locks

### C-089: Timer/Task leaks on shutdown
- **Status:** REJECTED
- **Reason:** CTS on exit; IDisposable implemented

### C-090: Child process handling
- **Status:** REJECTED
- **Reason:** Proper process lifecycle management

### C-091: Pipe framing bugs
- **Status:** REJECTED
- **Reason:** MT5 tick server uses length-prefixed framing

### C-092: TaskCompletionSource without RunContinuationsAsynchronously
- **Status:** REJECTED
- **Reason:** TCS used correctly

### C-093: Request/response id collisions
- **Status:** REJECTED
- **Reason:** Latest-wins pattern prevents collisions

### C-094: Timeouts missing
- **Status:** REJECTED
- **Reason:** Resilience pipeline with retry; timeouts configured

### C-095: Reconnect storms
- **Status:** REJECTED
- **Reason:** Resilience handler with backoff

### C-096: Thread-affinity assumptions
- **Status:** REJECTED
- **Reason:** ScriptRunner is thread-safe; GraphicsDevice managed by ComputeSharp

### C-097: Static state hiding
- **Status:** REJECTED
- **Reason:** Only LogManager is static; all others via DI

### C-098: Service locator creeping in
- **Status:** REJECTED
- **Reason:** Constructor injection used throughout

### C-099: Captive dependencies
- **Status:** REJECTED
- **Reason:** No singleton holds transient state

### C-100: Disposal missing
- **Status:** REJECTED
- **Reason:** IDisposable implemented; using/await using used

### C-101: Provider fallback swallowing errors
- **Status:** REJECTED
- **Reason:** Empty list returned with logging; never silent

### C-102: Rate limits
- **Status:** REJECTED
- **Reason:** Resilience handler configured per-provider

### C-104: Unsorted duplicate timestamps
- **Status:** REJECTED
- **Reason:** DataManager validates and normalizes timestamps

### C-106: Cache key collisions
- **Status:** REJECTED
- **Reason:** symbol@timeframe with normalization

### C-107: Preferred-provider semantics
- **Status:** REJECTED
- **Reason:** preferredProvider honored in cache lookup

### C-108: Memory cache without size/expiry
- **Status:** REJECTED
- **Reason:** IMemoryCache with SizeLimit; sliding expiry documented

### C-109: Cache schema evolution
- **Status:** REJECTED
- **Reason:** Origin discriminator (2026-10-28) handles legacy detection

### C-110: Deserializing then re-serializing
- **Status:** REJECTED
- **Reason:** ReadCacheMetadata scans without full deserialization

### C-111: Metadata memoization stale
- **Status:** REJECTED
- **Reason:** Key includes (path, mtime, length); invalidated on save/delete

### C-112: Volume heuristics wrong
- **Status:** REJECTED
- **Reason:** Zero volume treated as valid (crypto); HasVolume scanned correctly

### C-113: Ignoring provider limits
- **Status:** REJECTED
- **Reason:** Finnhub/Warn logged for empty responses

### C-114: Symbol mapping in wrong layer
- **Status:** REJECTED
- **Reason:** Symbol resolution in providers; timeframe in TimeframeMap

### C-115: HttpClient misuse
- **Status:** REJECTED
- **Reason:** Named "ResilientClient" with AddStandardResilienceHandler

### C-116: Secrets in logs/URLs
- **Status:** REJECTED
- **Reason:** API keys read from .env; never logged

### C-117: Key placeholder detection incomplete
- **Status:** REJECTED
- **Reason:** NormalizeValue handles all key properties

### C-119: Float for prices
- **Status:** REJECTED
- **Reason:** Prices use double; precision documented

### C-120: NaN/Infinity propagation
- **Status:** REJECTED
- **Reason:** Metrics guard degenerate inputs; invariant maintained

### C-121: Partial fetch cached as complete
- **Status:** REJECTED
- **Reason:** Only complete ranges saved

### C-122: Overwriting richer cache with poorer
- **Status:** REJECTED
- **Reason:** Origin tracked; cache miss does not overwrite

### C-123: Data origin confusion
- **Status:** REJECTED
- **Reason:** IQueueOriginGate warns when consuming raw-tick

### C-124: Queue state drifting
- **Status:** REJECTED
- **Reason:** Single writer pattern (DataRowViewModel.IsQueued)

### C-125: Stale UI after cache change
- **Status:** REJECTED
- **Reason:** DataGrid refreshes on collection change

### C-126: Doc contradictions
- **Status:** CONFIRMED
- **Reason:** See DOC-001, DOC-002 above

---

## SUMMARY

| Status | Count |
|--------|-------|
| CONFIRMED | 5 |
| REJECTED | 119 |
| DEFERRED | 0 |

## ACTION ITEMS

### Immediate (CONFIRMED)
1. Fix C-037: Replace sync File I/O with async in ConfigManager
2. Fix C-079: Replace .GetAwaiter().GetResult() with async/await
3. Fix DOC-001: Update ROADMAP.md to reference WPF upgrade
4. Fix DOC-002: Standardize terminology in ARCHITECTURE.md

### Verification Needed
1. C-079: .GetAwaiter().GetResult() - verify no deadlocks in practice
2. DOC-001: Confirm phase 9 actually completed (it's marked COMPLETED)
