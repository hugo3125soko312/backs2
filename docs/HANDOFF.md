# Handoff — session ending 2026-10-25

HEAD at handoff: 99a84b3. Working tree clean.

Read this before touching anything. It records what is verified, what is not,
and which mistakes have already been made so they are not repeated.

## Verified by measurement

- OOM fix (chunked coarse requests): 4,032 combinations completes in 8.6 s. Before
  the fix it died at 35 s with OutOfMemoryException.
- Slice baseline sharing: TimeSliceBuilder thread time 1,094,643 ms -> 15,397 ms.
- Log volume: 24,500 lines per Optimize run -> 169. Confirmed on runtime-20261025-184505.log.
- Phase 32 EasternTime: 500,000 random probes, 0 mismatches vs TimeZoneInfo.
- Batched dispatch determinism: 24 heterogeneous versions x 9 arrays, 0 mismatches,
  on both CPU and GPU backends.
- VRAM probe: --vram-verify reports 7188 MB budget on the RTX 4060.
- GPU probe: --gpu-verify reports 0% idle.
- Unfocused FPS cap: user-confirmed.

## Compile-verified only, never executed

- 601ab02 ETA defaults re-baselined.
- 9f4715e, dc05952 editable sampling cap in the Runner toolbar.
- Phase 36 metrics under sustained load.
- The whole of Phase 36D has been exercised by eye, not instrumented.

## Open items and the context they need

- Execution and batch log counters are PROCESS-LIFETIME, not per-run. Deliberate.
  A per-run reset belongs beside the [29A] accumulator resets in OptimizationRunner.
- GPU reads 0% during Optimize and that is CORRECT. The coarse pass runs 64-version
  chunks of 100 iterations, below the 400k GPU batch threshold, so the optimizer
  never dispatches to the card. Do not lower the threshold to make the diagnostic
  look busier. Recorded in ROADMAP Phase 36.
- GpuBatchMinIterations = 400_000 is interpolated from four measured points, not a
  swept curve. The crossover is real but the constant is a guess with a decimal point.
- CacheManager._metaCache is unbounded. One entry per cache file. Fine at hundreds,
  a slow leak at thousands.
- Phase 26D.2b/2c are the largest genuinely valuable unstarted work: optimizer
  min/max/step fields and the Optimize button wiring.

## Bridge operational knowledge

This section exists because these cost more time than any code problem.

1. exit=-1 means RESPONSE TIMEOUT, not operation failure. The command often
   succeeded. Always verify with a read before retrying. Two destructive bugs came
   from retrying an already-applied patch.

2. Two or more $ on one line are eaten as LaTeX and replaced with the word
   "dollars". A single $ survives. Any .Replace() whose anchor contains
   $"interpolation" silently does nothing. Use [char]36 or $ -free anchors.

3. Prose adjacent to a tag block is executed as shell commands. Keep responses
   either pure blocks or pure prose.

4. Constrained language mode blocks New-Object, .Add(), .Substring(), [math]::*.
   These fail SILENTLY inside a pipeline. This destroyed CpuComputeDispatcher.cs
   once (Set-Content wrote an empty file over 31 KB; git recovered it). Use raw
   string .Replace() only. Never build collections.

5. -N cannot create parent directories. It hangs rather than erroring.

6. -SimpleMatch treats | as a literal, not alternation.

7. Anchor drift: docs/ROADMAP.md is LF, code files are CRLF. Detect per file.
   A corrupted character (U+FFFD where an em-dash belonged) makes any literal
   anchor unmatchable; use regex with [^"]* instead.

8. Size limits: -N bodies over ~2 KB and -ER commands over ~1,500 chars time out.
   Split larger writes.

## Mistakes already made, so they are not repeated

All four share one cause: shipping without verifying runtime behaviour.

1. Startup crash. 515819d added RequestAnimationFrame and Services! inside
   OnFrameworkInitializationCompleted. It took the GUI down with 0xe0434352 before
   the first frame. Fixed by moving to MainWindow.Opened, where the compositor
   exists, with every step guarded. Root cause: never launched the app once.

2. CPU threshold in the wrong units. MaxCpuPercent = 85 was written meaning 85% of
   the machine; the metric computed percent-of-one-core, so a 20-core box reported
   1100% and the breach fired at ~4% real load. Fixed by dividing by ProcessorCount.
   Nothing in the type system makes a metric and its threshold agree on units.

3. A visibility gate that could never fire. The Logs VM gated flushing on
   Attached/DetachedFromVisualTree. Tab switching flips IsVisible on stacked
   ContentControls and the view never leaves the tree, so the gate was dead code.
   Fixed by driving it from OnSelectedNodeChanged.

4. Benchmarking at the wrong scale. The --mc-bench harness used 1,500-2,400 synthetic
   slices per version against a real dataset of 60,540 - 25x smaller. It could not
   have caught the OOM, and the GPU crossover number derived from it is still a
   four-point interpolation.

## Suggested first move

Run one Optimize pass with the Logs tab open. It is the only test that exercises the
log bucketing, the progress throttle, the GPU threshold and the backtest dispatch
together, and it is cheap. Everything else in the pending list can wait behind it.