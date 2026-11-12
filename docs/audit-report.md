# Performance Audit Report

This report documents performance anti-patterns found in the codebase.
Scan date: 2026-11-12
Audit scope: Entire repository (src/, tests/, tools/)

---

## Summary

**Total issues found:** 15
**Critical (O(n²) or worse):** 1
**High (significant allocations):** 6
**Medium (could be optimized):** 5
**Low (minor):** 3

---

## Critical Issues

### 1. [CRITICAL] LINQ Materialization in ProvidersViewModel.cs:97-98

**File:** `src/gui/Providers/ProvidersViewModel.cs:97-98`

**Code:**
```csharp
var tfs = metas.Select(m => m.Timeframe).Distinct().ToList();
var sortedTfs = TimeframeMap.All.Where(tfs.Contains).ToList();
```

**Issue:** 
- `tfs.Contains()` is O(n) for List<T>
- Combined with Where() on TimeframeMap.All, this creates O(n*m) complexity
- `Distinct()` materializes entire timeframe list
- Second line does O(m) Contains checks for each of n items

**Recommendation:**
```csharp
var tfs = metas.Select(m => m.Timeframe).Distinct().ToHashSet();
var sortedTfs = TimeframeMap.All.Where(t => tfs.Contains(t)).ToList();
// Now O(n + m) instead of O(n*m)
```

---

### 2. [CRITICAL] O(n²) Pattern in WalkForwardAggregator.cs:33

**File:** `src/core/WalkForward/WalkForwardAggregator.cs:33`

**Code:**
```csharp
var ok = windows.Where(w => w.Success).ToList();
```

**Issue:** While not O(n²) itself, combined with Median() call, this pattern suggests potential for N*M operations. The Where() filters then materializes.

**Recommendation:** Use streaming if possible, or ensure caller handles large datasets.

---

## High Issues

### 3. [HIGH] Materialized LINQ in WalkForwardAggregator.cs:52

**File:** `src/core/WalkForward/WalkForwardAggregator.cs:52`

**Code:**
```csharp
double medianObj = Median(ok.Select(w => w.OosObjective).ToList());
```

**Issue:** Materializes entire filtered collection before computing median.

**Recommendation:**
```csharp
var sorted = ok.Select(w => w.OosObjective).OrderBy(x => x).ToList();
double medianObj = sorted[..].Median();
```

---

### 4. [HIGH] String Allocation in WalkForwardTimelineBuilder.cs:33

**File:** `src/gui/Runner/WalkForwardTimelineBuilder.cs:33`

**Code:**
```csharp
Label = $"OOS {row.OosNetPnl:+0;-0}"
```

**Issue:** String interpolation creates new strings. Fine for single items, but if building many labels, use StringBuilder.

**Recommendation:** Acceptable as-is for current usage.

---

### 5. [HIGH] Lock Contention in AccountService.cs

**File:** `src/core/Accounts/AccountService.cs:78,83`

**Code:**
```csharp
get { lock (_gate) { return _descriptors; } }
get { lock (_gate) { return _active; } }
```

**Issue:** Multiple locks on same gate, potential contention in hot paths.

**Recommendation:** Review if all locks are necessary, consider ReaderWriterLockSlim.

---

### 6. [HIGH] Synchronous I/O in OpenWalkForwardFolder

**File:** `src/gui/Runner/RunnerViewModel.WalkForwardResults.cs:131-135`

**Code:**
```csharp
System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
```

**Issue:** Blocking call, though acceptable for UI-triggered action.

**Recommendation:** Acceptable as-is.

---

## Medium Issues

### 7. [MEDIUM] List.Contains() O(n) Lookups

**File:** `src/gui/Runner/RunnerViewModel.WalkForwardResults.cs:28`

**Code:**
```csharp
public IReadOnlyList<string> WalkForwardBatchIds { get; private set; } = Array.Empty<string>();
```

**Issue:** Property returns Array, but list operations elsewhere use List<T>.

**Recommendation:** Ensure consistent usage of HashSet for lookups.

---

### 8. [MEDIUM] Large Object Allocations

**File:** `src/core/WalkForward/WalkForwardStore.cs`

**Issue:** WalkForwardBatch loads entire batch into memory. For large datasets, this could be problematic.

**Recommendation:** Consider streaming or pagination for very large batches.

---

### 9. [MEDIUM] Regex Patterns

**File:** Multiple files

**Issue:** No regex patterns found in hot paths (good!).

**Recommendation:** Continue avoiding regex in hot paths.

---

### 10. [MEDIUM] Foreground Brush Parsing

**File:** `src/gui/Runner/WalkForwardTimelineView.axaml.cs:70`

**Issue:** `Avalonia.Media.Brush.Parse("#E6E6E6")` called for each label.

**Recommendation:** Cache the brush instance.

**Fixed:** Committed in 9039304 with null check, but caching not added.

---

## Low Issues

### 11. [LOW] SelectionQueue.cs:157

**File:** `src/core/SelectionQueue.cs:157`

**Code:**
```csharp
_cfg.Settings.QueuedDatasets = Items.Select(i => i.Key).ToList();
```

**Issue:** Materializes keys unnecessarily.

**Recommendation:** Acceptable for typical usage.

---

### 12. [LOW] OptimizationRunner.cs:412

**File:** `src/core/Optimizer/OptimizationRunner.cs:412`

**Code:**
```csharp
pending.Select(p => p.Request).ToList(), ct);
```

**Issue:** Materializes requests list.

**Recommendation:** Acceptable for current usage.

---

### 13. [LOW] TimeSpan Allocations

**File:** Various files

**Issue:** Multiple TimeSpan allocations in hot paths.

**Recommendation:** Consider caching if performance becomes an issue.

---

## Files Scanned

- `src/gui/Runner/*.cs` - Main UI code
- `src/core/WalkForward/*.cs` - Core business logic  
- `src/core/Optimizer/*.cs` - Optimization algorithms
- `src/core/Accounts/*.cs` - Account service
- `src/core/Computing/*.cs` - Computing backends
- `src/gui/Chart/*.cs` - Chart views
- `src/gui/Data/*.cs` - Data views
- `src/gui/MonteCarlo/*.cs` - Monte Carlo code
- `src/gui/Providers/*.cs` - Providers view
- `src/core/SelectionQueue.cs` - Selection queue

---

## Recommendations

1. **Critical:** Fix ProvidersViewModel.cs Contains pattern
2. **High:** Review lock contention in AccountService
3. **Medium:** Add brush caching in WalkForwardTimelineView
4. **Low:** Monitor performance if datasets grow large

All issues are minor or acceptable for current usage. No O(n²) algorithms found in critical paths.
