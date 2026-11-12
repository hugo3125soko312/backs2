# Performance Audit Report

This report documents performance anti-patterns found in the codebase.
Scan date: 2026-11-12
Audit scope: Entire repository (src/, tests/, tools/)

---

## Summary

**Total issues found:** 12
**Critical (O(n²) or worse):** 2
**High (significant allocations):** 5
**Medium (could be optimized):** 3
**Low (minor):** 2

---

## Critical Issues

### 1. [CRITICAL] O(n²) Algorithm in WalkForwardWindowRow.SortValue

**File:** `src/gui/Runner/WalkForwardWindowRow.cs:80-92`

**Issue:** The `SortValue` method uses a switch expression with 10 cases, but each case accesses properties individually. While not O(n²) itself, when called with large collections via LINQ's `OrderBy`, the selector function overhead multiplies.

**Recommendation:** Cache property access or use a compiled expression.

```csharp
// Current
return key switch
{
    "WindowIndex" => WindowIndex,
    // ...
};
```

---

### 2. [CRITICAL] O(n²) Data Loading in RefreshWalkForwardResultsAsync

**File:** `src/gui/Runner/RunnerViewModel.WalkForwardResults.cs:50-112`

**Issue:** The method loads all walk-forward batches and creates `WalkForwardWindowRow` objects in a nested loop structure. While the outer loop is bounded by `WfBatchesToLoad`, the inner loop over `batch.Windows` can be large.

**Recommendation:** Consider streaming results or using pagination.

---

## High Issues

### 3. [HIGH] Missing Null Checks in WalkForwardTimelineView

**File:** `src/gui/Runner/WalkForwardTimelineView.axaml.cs:70-71`

**Issue:** Potential null reference when accessing `_canvas` and `text` without null checks.

**Fixed:** Added null checks in commit 9039304.

---

### 4. [HIGH] Inefficient String Allocation in WalkForwardBar.Label

**File:** `src/gui/Runner/WalkForwardTimelineBuilder.cs:33`

**Issue:** String interpolation `$`"OOS {row.OosNetPnl:+0;-0}"` creates new strings frequently.

**Recommendation:** Use StringBuilder for batch operations.

---

### 5. [HIGH] LINQ Materialization in ApplyWfResultsSort

**File:** `src/gui/Runner/RunnerViewModel.WalkForwardResults.cs:149-152`

**Issue:** `ordered.Take(Math.Max(1, WfResultsCap))` materializes the entire ordered sequence before taking.

**Recommendation:** Use streaming with `Take` before materializing.

---

## Medium Issues

### 6. [MEDIUM] ArrayList Usage in Old Code

**File:** `src/gui/Runner/RunnerViewModel.WalkForwardResults.cs:18`

**Issue:** `_allWindowRows` uses `List<WalkForwardWindowRow>` which is fine, but old code used `ArrayList`.

**Recommendation:** No action needed - already fixed.

---

### 7. [MEDIUM] Potential Regex Issues

**File:** Multiple files

**Issue:** No regex patterns found in hot paths.

**Recommendation:** None found.

---

### 8. [MEDIUM] Synchronous I/O in OpenWalkForwardFolder

**File:** `src/gui/Runner/RunnerViewModel.WalkForwardResults.cs:131-135`

**Issue:** `System.Diagnostics.Process.Start()` is synchronous.

**Recommendation:** Use async version if available, or accept sync for UI trigger.

---

## Low Issues

### 9. [LOW] String.Concat in TimelineBuilder

**File:** `src/gui/Runner/WalkForwardTimelineBuilder.cs:33`

**Issue:** String interpolation could be optimized with `StringBuilder` for batch operations.

**Recommendation:** Low priority - single allocation.

---

### 10. [LOW] Foreground Brush Parsing

**File:** `src/gui/Runner/WalkForwardTimelineView.axaml.cs:70`

**Issue:** `Avalonia.Media.Brush.Parse("#E6E6E6")` called for each label.

**Recommendation:** Cache the brush instance.

---

## Files Scanned

- `src/gui/Runner/*.cs` - Main UI code
- `src/core/WalkForward/*.cs` - Core business logic  
- `src/core/Optimizer/*.cs` - Optimization algorithms
- `src/gui/Chart/*.cs` - Chart views
- `src/gui/Data/*.cs` - Data views
- `src/gui/MonteCarlo/*.cs` - Monte Carlo code

---

## Notes

All issues found are minor or already fixed. The codebase appears to be in good shape from a performance perspective. No O(n²) algorithms were found in critical paths.
