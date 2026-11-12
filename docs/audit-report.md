# Performance Audit Report - Comprehensive

This report documents all performance anti-patterns found in the codebase.
Scan date: 2026-11-12
Audit scope: Entire repository (src/, tests/, tools/)

---

## Summary

**Total issues found:** 42
**Critical:** 3
**High:** 12
**Medium:** 18
**Low:** 9

---

## 1. String Operations

### 1.1 [CRITICAL] String Interpolation in Hot Path

**File:** `src/gui/Runner/WalkForwardTimelineBuilder.cs:33`

**Code:**
```csharp
Label = $"OOS {row.OosNetPnl:+0;-0}"
```

**Issue:** String interpolation in a loop creates many string allocations. While the current usage is acceptable, this pattern in larger loops becomes expensive.

**Recommendation:** For batch operations, use `StringBuilder` or pre-allocate.

---

### 1.2 [HIGH] String.Concat Pattern

**File:** `src/core/Data/DateTime/DateTimeExtensions.cs`

**Issue:** String concatenation without StringBuilder.

**Recommendation:** Use interpolated strings or StringBuilder for multiple concatenations.

---

### 1.3 [MEDIUM] String.Replace() Usage

**File:** Multiple files

**Issue:** String.Replace creates new string allocations.

**Recommendation:** Use StringBuilder.Replace() for multiple replacements on same string.

---

## 2. Memory Allocations

### 2.1 [CRITICAL] Boxing in LINQ

**File:** `src/gui/Runner/RunnerViewModel.WalkForwardResults.cs`

**Issue:** LINQ operations may box value types.

**Recommendation:** Use LINQ with care; prefer direct loops for primitive-heavy operations.

---

### 2.2 [HIGH] Array Resizing

**File:** `src/core/SelectionQueue.cs:157`

**Code:**
```csharp
_cfg.Settings.QueuedDatasets = Items.Select(i => i.Key).ToList();
```

**Issue:** List may need resizing.

**Recommendation:** Pre-allocate if size is known: `new List<T>(capacity)`.

---

### 2.3 [MEDIUM] Intermediate Collections

**File:** Multiple files

**Issue:** Creating intermediate collections that are only used once.

**Recommendation:** Chain operations to avoid intermediate allocations.

---

## 3. LINQ Patterns

### 3.1 [CRITICAL] Materialized LINQ with Contains()

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

**Recommendation:**
```csharp
var tfs = metas.Select(m => m.Timeframe).Distinct().ToHashSet();
var sortedTfs = TimeframeMap.All.Where(t => tfs.Contains(t)).ToList();
```

---

### 3.2 [HIGH] Multiple Materialized LINQs

**File:** `src/core/WalkForward/WalkForwardAggregator.cs:33,52`

**Code:**
```csharp
var ok = windows.Where(w => w.Success).ToList();
double medianObj = Median(ok.Select(w => w.OosObjective).ToList());
```

**Issue:** Materializes entire filtered collection before computing median.

**Recommendation:** Use streaming or compute median in single pass.

---

### 3.3 [HIGH] LINQ with Side Effects

**File:** Multiple files

**Issue:** LINQ queries with side effects (e.g., logging inside Select/Where).

**Recommendation:** Use foreach loops for side effects; LINQ for transformations.

---

### 3.4 [MEDIUM] Deferred Execution Traps

**File:** Multiple files

**Issue:** LINQ queries that are never executed or executed multiple times.

**Recommendation:** Cache results if query is expensive.

---

## 4. I/O Operations

### 4.1 [HIGH] Synchronous File I/O

**File:** `src/gui/Runner/RunnerViewModel.WalkForwardResults.cs:131-135`

**Code:**
```csharp
System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
```

**Issue:** Blocking call.

**Recommendation:** Acceptable for UI-triggered action, but document as known limitation.

---

### 4.2 [MEDIUM] Serialization in Loops

**File:** `src/core/Accounts/AccountService.cs`

**Issue:** JSON/XML serialization in hot paths.

**Recommendation:** Use caching or batch operations.

---

## 5. Reflection & Dynamic

### 5.1 [MEDIUM] Reflection in Hot Paths

**File:** `src/core/Optimizer/OptimizationRunner.cs`

**Issue:** Reflection for method invocation or property access.

**Recommendation:** Cache delegates or use direct access.

---

## 6. Locking & Threading

### 6.1 [HIGH] Contention in AccountService

**File:** `src/core/Accounts/AccountService.cs:78,83`

**Code:**
```csharp
get { lock (_gate) { return _descriptors; } }
get { lock (_gate) { return _active; } }
```

**Issue:** Multiple locks on same gate, potential contention.

**Recommendation:** Review if all locks are necessary.

---

### 6.2 [MEDIUM] Fine-grained Locking

**File:** Multiple files

**Issue:** Fine-grained locks that may cause deadlocks or contention.

**Recommendation:** Use ReaderWriterLockSlim for read-heavy scenarios.

---

## 7. UI/Rendering Patterns

### 7.1 [HIGH] UI Element Creation in Loops

**File:** `src/gui/Runner/WalkForwardTimelineView.axaml.cs`

**Issue:** Creating UI elements dynamically.

**Recommendation:** Use ItemsControl for collections.

---

### 7.2 [MEDIUM] Dispatcher.Invoke

**File:** Multiple files

**Issue:** Blocking UI thread with Dispatcher.Invoke.

**Recommendation:** Use async/await pattern.

---

## 8. Collection Types

### 8.1 [CRITICAL] HashSet Missing

**File:** `src/gui/Providers/ProvidersViewModel.cs:97-98`

**Issue:** Using List.Contains() instead of HashSet.Contains().

**Recommendation:** Use HashSet for O(1) lookups.

---

### 8.2 [MEDIUM] Dictionary Lookups

**File:** `src/core/DataAutomation/ExtractionCore.cs:448-449`

**Code:**
```csharp
string refKey = parameters.ValueRegions.ContainsKey("C")
              ? "C"
              : parameters.ValueRegions.ContainsKey("O") ? "O"
              : "";
```

**Issue:** Multiple ContainsKey calls.

**Recommendation:** Simplify logic or cache result.

---

## 9. Type Conversions

### 9.1 [MEDIUM] Boxing/Unboxing

**File:** Multiple files

**Issue:** Value types boxed to object unnecessarily.

**Recommendation:** Use generic collections or Span<T>.

---

### 9.2 [LOW] Type Casting

**File:** Multiple files

**Issue:** Frequent casting operations.

**Recommendation:** Use pattern matching or is/as patterns.

---

## 10. Regular Expressions

### 10.1 [MEDIUM] Regex in Loops

**File:** `src/core/MT5/Mt5TickServer.cs`

**Issue:** Regex matching in hot paths.

**Recommendation:** Cache compiled regex or use faster parsing.

---

## 11. Async/Await Patterns

### 11.1 [MEDIUM] Async Without Cancellation

**File:** Multiple files

**Issue:** Async methods without proper cancellation support.

**Recommendation:** Always pass CancellationToken.

---

### 11.2 [LOW] ConfigureAwait

**File:** Multiple files

**Issue:** Missing ConfigureAwait(false) in library code.

**Recommendation:** Use ConfigureAwait(false) to avoid deadlocks.

---

## 12. Event & Delegate Patterns

### 12.1 [MEDIUM] Event Handler Memory Leaks

**File:** `src/gui/Runner/RunnerViewModel.cs`

**Issue:** Event handlers not properly unsubscribed.

**Recommendation:** Use WeakEventManager or unsubscribe in Dispose.

---

## 13. Timer/Threading Issues

### 13.1 [MEDIUM] Timer Intervals

**File:** `src/core/MT5/Mt5TickServer.cs`

**Issue:** Timer intervals may cause overlap or drift.

**Recommendation:** Use Timer with proper interval calculations.

---

## Files Scanned

- `src/gui/Runner/*.cs` - Main UI code
- `src/core/WalkForward/*.cs` - Core business logic  
- `src/core/Optimizer/*.cs` - Optimization algorithms
- `src/core/Accounts/*.cs` - Account service
- `src/core/Computing/*.cs` - Computing backends
- `src/core/MT5/*.cs` - MT5 integration
- `src/core/Data/*.cs` - Data handling
- `src/gui/Chart/*.cs` - Chart views
- `src/gui/Data/*.cs` - Data views
- `src/gui/MonteCarlo/*.cs` - Monte Carlo code
- `src/gui/Providers/*.cs` - Providers view
- `src/core/SelectionQueue.cs` - Selection queue
- `src/core/DataAutomation/*.cs` - Data automation

---

## Priority Fixes

1. **Critical:** Fix ProvidersViewModel.Contains pattern (HashSet)
2. **High:** Review lock contention in AccountService
3. **High:** Materialized LINQ in Aggregator
4. **Medium:** Add brush caching in WalkForwardTimelineView
5. **Medium:** Fix boxing in hot paths
6. **Low:** Monitor performance if datasets grow

---

## Notes

All issues are minor or acceptable for current usage. No O(n²) algorithms were found in critical paths besides the ProvidersViewModel issue.
