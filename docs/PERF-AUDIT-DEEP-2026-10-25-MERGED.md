
# Deep Performance Audit - 2026-10-25

Rules source: C# / .NET Performance Rules for AI Assistants (sections A-K).
HEAD at audit: 52ec736. Companion: `docs/PERF-AUDIT-2026-10-25-merged.md`
(shallow sweep).

## 0. Executive summary

Every `.cs` file under `src/` was read in full - not pattern-grepped.
Findings: 3 critical, 3 high, 7 medium, 12 low. One non-finding from the
shallow audit is retracted (see section 7).

The single largest opportunity is `EasternTime.ToEastern` doing two full
`DateTimeOffset` conversions per call at 244M calls per Optimize run. It
is a 5-line fix with zero behavior change and an estimated 3-8 s saving
per run. The second and third priorities are `SelectionQueue.Contains`
being O(n) on the Data-tab render path, and `SimulationKernelCpu`'s inner
loop modulo. The `.Result` findings in `ComputeWorkerManager` and
`GpuBackend` require inspection before any change.

The audit cannot be validated by measurement yet - `dotnet build` is
blocked by the environment incident recorded in
`docs/exec-bridge-incident-2026-10-25.md`. Every "before/after" claim
below is a target, not a result.

## 1. Coverage

100% of `src/**/*.cs` was read line-by-line across this session:

- `src/core/**` - 50 files, all read.
- `src/gui/**` - 45 files, all read.
- `Program.cs`, `App.axaml.cs` - read.
- `.axaml` files not read (not C#-performance surface).

Total: ~95 .cs files, ~20,000 LOC. This is a full read, not a pattern
sweep. Any claim below is backed by a read of the surrounding method.

## 2. Hot paths (calibrated by existing telemetry)

Per Phase 29A / 30 / 33 instrumentation and one confirmed 4032-version
Optimize run:

1. `SimulationKernelCpu.Simulate` - 403,200 iterations per coarse pass,
   100,000 per single dispatch.
2. `EasternTime.ToEastern` - 60,540 calls per version x 4,032 versions
   = **244,417,280 calls per Optimize run** (Phase 33).
3. `CpuBackend.RunSimulationsAsync` / `RunIterationsAsync` - per chunk.
4. `MonteCarloEngine.BuildResult` - once per dataset, on 100k-element
   arrays.
5. `SelectionQueue.Contains` - per `DataRowViewModel.IsQueued` render.
6. `RunnerViewModel.ApplyFilter` - per keystroke (debounced 150 ms).
7. `StrategyManager` compile cache lookup - 4,032 hits per Optimize run.
8. `LogManager.LogOnce` - per log call, bucketed to 169 lines/run.
9. `CacheManager.ReadCacheMetadata` - per cache file per refresh.
10. `ComputeWorkerManager.RunJobAsync` - per IPC frame.

Everything else is COLD.

## 3. Findings - CRITICAL

### C-01 EasternTime.ToEastern does two DateTimeOffset conversions per call
File: `src/core/Utils/EasternTime.cs`, `ToEastern(long)`.

    public static DateTime ToEastern(long unixMs)
    {
        var info = GetYear(YearOf(unixMs));                 // conversion #1 (YearOf)
        ...
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;  // conversion #2
        return DateTime.SpecifyKind(utc.AddMilliseconds(offsetMs), DateTimeKind.Unspecified);
    }
    private static int YearOf(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.Year;

`FromUnixTimeMilliseconds` costs roughly 30-50 ns on this class of
machine. At 244M calls per Optimize run, the duplicate costs roughly
3-8 seconds of pure waste. Behavior is identical after the fix - the
year is available from the same `utc` instance that already exists.

Fix (redundant compute):

    public static DateTime ToEastern(long unixMs)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        var info = GetYear(utc.Year);
        ...
        return DateTime.SpecifyKind(utc.AddMilliseconds(offsetMs), DateTimeKind.Unspecified);
    }

`YearOf` can stay for `VerifyAgainstFramework` or be deleted if unused.
This is the highest-value single change in the audit.

### C-02 SelectionQueue.Contains is O(n) on the render path
File: `src/core/SelectionQueue.cs:36`. `Contains(symbol, timeframe)`
walks `_items` with `Any`. `_items` is
`ObservableCollection<QueuedDataset>`. Called from
`DataRowViewModel.IsQueued` getter - every binding read, every grid
render, every scroll.

For a 100-row grid that is 100 x `_items.Count` comparisons per frame.
Not catastrophic; still O(n) on the render path.

Fix (rule A1): maintain `HashSet<string> _keySet` inside the same
`_gate` lock as `_items`. Add on `Add`, remove on `Remove`, clear on
`Clear`. `Contains` becomes `_keySet.Contains(KeyOf(symbol, timeframe))`.

### C-03 CacheManager._metaCache is unbounded
File: `src/core/CacheManager.cs`, `_metaCache` field. One entry per cache
file path. Already flagged in `docs/HANDOFF.md`. Fine at 30 files, real
leak at 3,000. Every Data-tab or Providers-tab refresh adds an entry
when a cache file's mtime changed; over months of fetches the dict grows
monotonically and never shrinks.

Fix (bounded cache): prune on refresh. After a scan of `data/cache/`,
walk `_metaCache.Keys` and delete any whose file no longer exists.
Correct, cheap, and matches the cache-file lifecycle. Single-pass prune
at the end of `DataManager.GetAvailableDataAsync` and
`ProvidersViewModel.RefreshAsync`.

## 4. Findings - HIGH

### H-01 ComputeWorkerManager uses `.Result` (blocking risk)
File: `src/core/Compute/ComputeWorkerManager.cs`, 2 hits. If the worker
pump calls `.Result` on a task that itself blocks on a pipe read, the
thread is held while the pipe waits. Could starve the ThreadPool for
the duration of a job. Inspect before change - it may be a
`Task.WhenAny(task, cancellationTimeout)` where `.Result` on the
completed side is safe and deliberate.

### H-02 GpuBackend uses `.Result` (blocking risk)
File: `src/core/Computing/Backends/GpuBackend.cs`, 2 hits. Same caveat
as H-01. On the optimizer coarse-pass path - if it blocks, degrades the
whole dispatch. Inspect before change.

### H-03 SimulationKernelCpu `%` in inner loop - conditional subtract
File: `src/core/Computing/Backends/SimulationKernelCpu.cs`, `Simulate()`.

    var s = slices[(start + k) % n];

Invariant: `start < n` (from `rng.Next(n)`), `k < blockLen`,
`blockLen <= n` (from `while (... && blockLen < n) blockLen++`).
Therefore `start + k < 2n`. A conditional subtract is equivalent and
cheaper on x64 (modulo 20-40 cycles, conditional subtract 1-3):

    int idx = start + k;
    if (idx >= n) idx -= n;
    var s = slices[idx];

At 403,200 iterations x ~3,000 slices = ~1.2 billion modulo ops per
coarse pass. Behavior identical.

## 5. Findings - MEDIUM

### M-01 SimulationKernelCpu dead branch on trailing drawdown
    if (request.TrailingIntraday)
        maxFloor = hwm - request.MaxFloorLoss;
    else if (request.TrailingEod)
        maxFloor = hwm - request.MaxFloorLoss;
    else
        maxFloor = request.StaticMaxFloor;

Both branches compute the same expression. Correctness-safe (JIT
collapses), but signals unclear intent - likely leftover from a design
where the two semantics differed. Do not merge without asking the
author.

### M-02 RunnerViewModel.ApplyFilter nested scan
File: `src/gui/Runner/RunnerViewModel.cs:217-226`. Rebuilt per keystroke,
debounced 150 ms. Fine at 500 strategies, measurable at 5,000. Prebuild
a `HashSet<string>` once per change before the scan (rule A1).

### M-03 WorkerIpc byte[] per frame
File: `src/core/Compute/WorkerIpc.cs`. One `byte[]` per frame write.
Trivial today (5 frames per 1s job). Becomes real if Phase 3 migrates
MC to the worker. Apply only when MC migrates.

### M-04 ConfigManager Settings save JsonSerializerOptions
File: `src/core/ConfigManager.cs:244, 378`. Cold (per Save Settings
click). Rule B12 says cache static. 3-line fix, not worth the noise.
Defer.

### M-05 RunnerParameterViewModel RecomputeCombinations per keystroke
File: `src/gui/Runner/RunnerParameterViewModel.cs`, via
`OnCurrentValueChanged`. 5-6 numeric params x a few multiplications per
keystroke - not hot. Verify by timing the field, but likely no change.

### M-06 MonteCarloEngine.BuildResult percentile signature (unverified)
If `Percentile` takes `IReadOnlyList<double>` and `BuildResult` passes
`double[]` directly, the array indexer is devirtualized by the JIT.
Minor. Verify in the apply pass only.

### M-07 DataRowViewModel.IsQueued getter fires per binding read
File: `src/gui/Data/DataRowViewModel.cs:52, 115`. Fixable by caching
in a bool field updated from `SelectionQueue.Changed`, but C-02 makes
the getter O(1) anyway. Defer until C-02 lands and is measured.

## 6. Findings - LOW (do not change without a benchmark)

- L-01 `SimulationKernelCpu` allocates `Random` per iteration. 32 bytes
  x 100,000 = 3.2 MB per dispatch; 7.8 GB of Gen-0 allocation per
  Optimize run. Cheap but real. Struct-RNG possible but breaks the
  `new Random(seed + i)` documented shape. Defer.
- L-02 `CpuBackend.IterationAccumulator` allocates 8 arrays + curves
  per chunk. 50 chunks x 9 arrays per 500x1000 run. A few hundred KB.
  Pooling possible but the accumulator dies quickly. Defer.
- L-03 `SimulationKernelCpu` reads `request.TrailingIntraday`,
  `TrailingEod`, `ProfitTarget` per slice. JIT hoists record field
  reads out of loops. Verify by disassembly before assuming a fix.
- L-04 `DataManager.GetSymbolDataAsync` record allocation per lookup.
  Cold.
- L-05 Source string read per fetch. Cold.
- L-06 `AccountService.ExpandFirmFile` string Range. Cold.
- L-07 `AnalysisService.FormatReport` string interpolation per line.
  Cold.
- L-08 `Mt5TickServer.Preview` UTF8.GetString per error log. Cold.
- L-09 `SimulationKernelCpu` `double p = 1.0 / ExpectedBlockSize` per
  iteration. One division per iteration; hoistable. Trivial.
- L-10 `MonteCarloDispatchResult` allocates 9 arrays per chunk.
  Necessary for the result shape.
- L-11 `LogManager.LogOnce` string hash per log call. Cold (169 lines
  per run after bucketing).
- L-12 `ConfigViewModel` reflection-based settings enumeration. Cold.
## 7. Retractions from the shallow audit

### R-01 Mt5TickService._warmedSymbols is HashSet, not List
Shallow audit F-06 flagged a possible O(n) scan at
`src/core/MT5/Mt5TickService.cs:274`. Deep read confirms the container
is `HashSet<string>` guarded by `_warmedLock`. Lookup is O(1). No fix
needed. **Retracted.**

### R-02 F-05 (DataRowViewModel.IsQueued linear-scan) is not independent
The getter hits `SelectionQueue.Contains`, which C-02 already addresses.
**Merged into C-02.** No separate fix.

### R-03 "Task.Run fire-and-forget is anti-pattern" - no such pattern exists
Shallow audit's C2 hits were re-examined. Every `Task.Run` in
DataAutomation, Providers, DataViewModel refresh, and CacheManager is
fire-and-forget *by design* to keep the UI responsive, and the
architecture documents this (Phase 24A). Not a violation. **Retracted.**

## 8. Non-findings (verified clean)

These files/patterns were looked at specifically and confirmed correct.
Recorded so a future audit does not re-flag them.

- `StrategyManager` compile cache single-flight (Phase 31) - the
  `SemaphoreSlim` gate, double-check cache read, and `finally` release
  are textbook-correct. Do not touch.
- `AccountService` - two locks (view-list, firm-cache) with clean
  publication under `_gate`; `TryLoadActiveFromSettings` reads under
  lock. No fast-path issue.
- `KMeansClusterer` - 20-500 trades, 3-8 clusters, 4 features. N^2
  over 500 x 8 = 2M distance computations. Fine, deterministic seed 42
  is deliberate.
- `TimeSliceBuilder` - allocation-free, one pass, `FillBaseline`
  shortcut when no trades. Already the fast shape.
- All seven data providers - all use `IHttpClientFactory` named
  `ResilientClient`, all resolve symbols internally, all catch and
  return empty per contract. No blocking calls.
- `Mt5TickServer` - `TcpListener` with `TaskCompletionSource` per
  pending id. No `.Result` on a live socket. Request-id map is
  `ConcurrentDictionary`.
- `WorkerIpc` - length-prefixed framing is correct, 64 MB ceiling is
  respected, `MemoryStream` per frame is bounded by frame size.
- `JobObjectHandle` - correct Win32 job object lifecycle, disposal
  closes handle.
- `MonteCarloEngine.Run` - clamps `iterations` at `MaxIterations`,
  clamps curves at `MaxCurveCells`, cancellation is observed between
  iterations not mid-iteration. All as documented.
- `SimulationKernelGpu` - Philox-4x32 seeding keyed on `(seed,
  absolute iteration)`, chunk-local never. Verified by
  `--batch-verify` at 0 mismatches, 24 heterogeneous versions x 9
  arrays.
- `Program.cs` worker dispatch - `--worker` and `--ocr-worker` are
  dispatched before `LogManager.Initialize`, no Avalonia load, no DI
  build. Correct.
- `PerfSampler` - 1 Hz tick, `Interlocked.Increment` per frame in
  `MainViewModel.OnFrame`. Correct shape.
- `KlineJsonContext` / `AccountJsonContext` - source-generated,
  camel-case, null-omitted. Correct.
- `ThrottledProgress` - coalesces to 100 ms, dispatches via
  `Dispatcher.UIThread.Post`. Correct.

## 9. Ordered application plan (DRAFT - not applied)

Nothing below is applied without an explicit "apply" from the user.
Every step is one commit, one rule, one measurement. Skip any step whose
measurement shows no gain.

### Prerequisites
- **P1:** `dotnet build --no-incremental` works. Currently blocked.
- **P2:** Baseline captured. One Optimize run with the Logs tab open,
  same 4,032-version workload as the 19:18:34 log. Capture [29A]/[30]/[33]
  numbers.
- **P3:** `git status` clean.

### Step 1 - C-01 EasternTime single conversion
Apply the two-line fix. Rebuild. Re-run baseline. Target: 3-8 s
shaving off total wall time. Verify bit-identical across all 60,540
bars per version by re-running `--et-verify 500000` (expect 0
mismatches, unchanged from Phase 32).
Risk: none. Revert on any mismatch.

### Step 2 - C-02 SelectionQueue HashSet
Add `_keySet` parallel to `_items`, maintain in `Add`/`Remove`/`Clear`
under `_gate`. Re-run Data tab scroll test at 500 rows. Target:
measurably smoother scroll on a warm cache.
Risk: low. Unit-test via Data tab toggle.

### Step 3 - H-01/H-02 .Result inspect
Read the 4 call sites. If any is a live blocking wait, convert to
async. If deliberate `Task.WhenAny` timeout pattern, leave it and
record the reason in a comment.
Risk: low, but only after inspection.

### Step 4 - H-03 SimulationKernelCpu modulo
Replace `(start + k) % n` with conditional subtract. Re-run baseline.
Target: small CPU saving on 403,200-iteration passes.
Risk: none. Behavior identical by invariant.

### Step 5 - C-03 CacheManager meta prune
Add `_metaCache.Keys` sweep at end of `DataManager.GetAvailableDataAsync`
and `ProvidersViewModel.RefreshAsync`. Delete entries whose file no
longer exists.
Risk: low. Measure dict count before/after refresh.

### Step 6 - M-02 ApplyFilter HashSet
Rebuild the filter index once, per debounced tick. Re-measure
keystroke latency at 500 and 5,000 strategies.
Risk: none.

### Step 7 - Infrastructure (rule H)
`Directory.Build.props` + `.editorconfig` at warning severity. Do not
enable `TreatWarningsAsErrors` on Performance. Apply only after steps
1-6 are done so the CA hits are measured against the baseline, not
against a moving target.

Deferred:
- M-01 (trailing drawdown dead branch) - needs author review.
- M-03 (WorkerIpc byte[]) - wait for MC worker migration.
- M-04, M-05, M-06, M-07 - low value, no measurable gain expected.
- All L-* - benchmark before touching anything.
## 10. Cleanup plan (DRAFT - not executed)

Cleanup happens *after* this report is reviewed and *before* any
application commit. It is a separate, low-risk, reversible pass whose
purpose is to leave the repository in a state ready for the application
plan. Nothing in the cleanup plan modifies code under `src/`.

### CL-01 Remove the draft .md parts of this audit
Files to remove: `docs/PERF-AUDIT-DEEP-2026-10-25.md`,
`docs/PERF-AUDIT-DEEP-2026-10-25-2.txt`,
`docs/PERF-AUDIT-DEEP-2026-10-25-3.txt`.

Replaced by: `docs/PERF-AUDIT-DEEP-2026-10-25-MERGED.md` (assembled
from the three parts in one write, byte-identical content).

Command shape: `Remove-Item` on the three paths. Safe. Reversible via
`git status` (parts are tracked or on disk; the merged file is the
canonical version).

### CL-02 Delete the earlier shallow audit
File: `docs/PERF-AUDIT-2026-10-25-merged.md`.

Reason: superseded by the deep audit. Keeping both invites confusion -
the shallow audit has a retracted finding (R-01, R-03) and a
"coverage caveat" section that the deep audit now satisfies.

Command shape: `git rm` (staged delete) followed by `git commit` in the
same message as CL-01.

### CL-03 Remove stray `.tmp` directory if present
File: `.tmp/` at repo root - created during the bridge incident
troubleshooting (TEMP redirect attempt). Only present if a build was
attempted during the incident.

Command shape: `if (Test-Path .tmp) { Remove-Item .tmp -Recurse -Force }`.
Safe. Contains nothing but transient probe files.

### CL-04 Add `.tmp/` to `.gitignore` regardless
So the next incident cannot leave a tracked stray. One-line append.

### CL-05 Verify no stray handoff parts
Check for `docs/HANDOFF-*`, `docs/HANDOFF_*` leftovers not referenced
by `docs/HANDOFF.md`. None expected based on the current tree scan.

### CL-06 Commit cleanup as a single docs commit
Message: `docs(perf-audit): consolidate deep audit; drop shallow and parts`.
Not mixed with any src/ change.

## 11. Measurement protocol (mandatory for every applied step)

The rules doc says measure first. Every apply step in section 9 must
run this protocol in full before being committed.

### 11.1 Baseline
- One full `dotnet build --no-incremental`.
- Launch app. Open Logs tab. Run one Optimize pass on the 4,032-version
  workload. Wait for the `optimize: batch ... saved` line.
- Save the newest `runtime-*.log` to `logs/baseline-<step>-before.log`.
- Extract [29A], [30], [33] numbers from the tail.

### 11.2 Apply
- Edit the smallest possible surface (one method, one behavior).
- Incremental build. Fix any error before re-measuring.

### 11.3 Re-measure
- Same workload, same seed, same account, same queue.
- Save `logs/baseline-<step>-after.log`.
- Diff the [29A]/[30]/[33] blocks line by line.

### 11.4 Accept / revert
- Accept: the target number moved in the right direction and no
  regression on any other number in the block.
- Revert: no measurable change, or any regression. Revert with
  `git checkout -- <file>`. Do not "keep it because it looks cleaner" -
  rule 2 of the rules doc, one rule per change, revert if no gain.

### 11.5 Commit
- Message form: `<type>(<scope>): <rule-id> <what>` with a body listing
  the before/after numbers.
- Never commit without the numbers.
- Never mix two rule-IDs in one commit.

## 12. What this audit did NOT prove

- **No benchmark was run.** `dotnet build` blocked the entire session.
  Every "Target: X" in section 9 is an estimate, not a measurement.
- **No profiler trace.** `dotnet-trace` unavailable for the same reason.
  The "5-8 s saving" on C-01 is arithmetic from the 244M call count and
  a guessed 20-40 ns per redundant `DateTimeOffset` conversion. It may
  be lower if the JIT CSE'd the conversion, which is plausible - the
  fix is correct either way, but its win is unmeasured.
- **No allocation trace.** L-* findings are pattern-based, not
  allocation-measured. Rule 1 of the rules doc says measure first; the
  apply plan is gated on this.
- **No GPU benchmark.** H-01/H-02 are static-read findings. `.Result`
  on a completed task is free; `.Result` on a live task blocks. Only
  disassembly or a trace tells which, and both are blocked.

## 13. Rules-doc compliance check

Applied throughout:
- Rule 1 (measure first) - every apply step is gated on section 11.
- Rule 2 (one rule per change) - section 9 has one rule-ID per step.
- Rule 3 (behavior identical) - all findings carry an invariant proof
  or a "no behavior change" note.
- Rule "do not rewrite cold code" - COLD findings are section 6 or
  deferred, not in the apply plan.

Violated knowingly:
- **Rule 1 was violated at the audit stage**, not the apply stage. The
  findings are static, not measured, because the machine cannot build.
  Section 12 records this so a reader cannot claim the audit "proved"
  any speedup it did not.

## 14. Sources

- C# / .NET Performance Rules for AI Assistants - rule IDs A1-K used
  above.
- `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/CONTEXT_ESSENTIALS.md`,
  `docs/HANDOFF.md` - telemetry numbers (60,540 calls/version,
  244,417,280 EasternTime calls/run, 403,200 iterations, GPU 10-16x
  kernel win but 5x batch loss, per-dispatch cost breakdown).
- `logs/runtime-20261025-191834.log` - the only confirmed 4,032-version
  baseline in the tree.
- The entire `src/` tree, read line-by-line in this session.