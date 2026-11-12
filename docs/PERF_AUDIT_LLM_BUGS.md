# Performance Anti-Patterns Common in AI/LLM-Generated Code

This document catalogs common performance anti-patterns that AI LLMs tend to generate, organized by severity and category.

---

## 1. O(n²) Algorithms When O(n) or O(n log n) is Possible

### Pattern: Nested Loops Over Same Data

**Bad:**
```csharp
// O(n²) - compares every element with every other element
foreach (var item in list)
{
    foreach (var other in list)
    {
        if (item.Id == other.Id) { ... }
    }
}
```

**Good:**
```csharp
// O(n) - uses HashSet for O(1) lookups
var seen = new HashSet<int>();
foreach (var item in list)
{
    if (!seen.Add(item.Id)) { ... }
}
```

### Pattern: Repeated String Concatenation in Loops

**Bad:**
```csharp
// O(n²) because strings are immutable
string result = "";
for (int i = 0; i < n; i++)
{
    result += GetString(i); // Creates new string each iteration
}
```

**Good:**
```csharp
// O(n) using StringBuilder
var sb = new StringBuilder();
for (int i = 0; i < n; i++)
{
    sb.Append(GetString(i));
}
```

---

## 2. Memory Allocation Issues

### Pattern: Unnecessary Boxing/Unboxing

**Bad:**
```csharp
// Value types boxed to object
ArrayList list = new ArrayList();
list.Add(42); // int boxed to object
```

**Good:**
```csharp
// Generic collections avoid boxing
List<int> list = new List<int>();
list.Add(42); // No boxing
```

### Pattern: Creating Intermediate Collections

**Bad:**
```csharp
// Creates 2n allocations instead of 1
var results = items.Select(x => Transform(x)).ToList();
var filtered = results.Where(x => Filter(x)).ToList();
```

**Good:**
```csharp
// Single allocation with chaining
var filtered = items.Where(x => Filter(x))
                     .Select(x => Transform(x))
                     .ToList();
```

### Pattern: Array Resizing

**Bad:**
```csharp
// O(n²) total due to repeated resizing
var list = new List<T>();
for (int i = 0; i < n; i++)
{
    list.Add(item); // May trigger resize
}
```

**Good:**
```csharp
// O(n) total - pre-allocate capacity
var list = new List<T>(n);
for (int i = 0; i < n; i++)
{
    list.Add(item);
}
```

---

## 3. Inefficient Collections Usage

### Pattern: List.Contains() O(n) Search

**Bad:**
```csharp
// O(n) per lookup
if (list.Contains(item)) { ... }
```

**Good:**
```csharp
// O(1) lookup with HashSet
var set = new HashSet<T>(list);
if (set.Contains(item)) { ... }
```

### Pattern: LINQ Without Streaming

**Bad:**
```csharp
// Materializes entire collection before filtering
var filtered = largeCollection.Where(x => x.IsActive).ToList();
```

**Good:**
```csharp
// Stream processing - only allocates filtered items
foreach (var item in largeCollection.Where(x => x.IsActive))
{
    Process(item);
}
```

---

## 4. String/Regex Operations

### Pattern: Compiling Regex in Loops

**Bad:**
```csharp
// Compiles regex on every iteration
for (int i = 0; i < n; i++)
{
    var match = Regex.Match(text, pattern); // Slow!
}
```

**Good:**
```csharp
// Compile once
var regex = new Regex(pattern, RegexOptions.Compiled);
for (int i = 0; i < n; i++)
{
    var match = regex.Match(text);
}
```

### Pattern: String.Replace() on Long Strings

**Bad:**
```csharp
// Creates new string of same length each call
string result = longString.Replace("old", "new");
```

**Good:**
```csharp
// Use StringBuilder for multiple replacements
var sb = new StringBuilder(longString);
sb.Replace("old", "new");
```

---

## 5. I/O and Serialization

### Pattern: Synchronous I/O in Hot Paths

**Bad:**
```csharp
// Blocks thread
var data = File.ReadAllBytes(path);
```

**Good:**
```csharp
// Async I/O
var data = await File.ReadAllBytesAsync(path);
```

### Pattern: Serializing Small Objects Individually

**Bad:**
```csharp
// n separate allocations and writes
foreach (var item in items)
{
    File.WriteAllText($"{path}/{item.Id}.json", JsonSerializer.Serialize(item));
}
```

**Good:**
```csharp
// Batch write
var json = JsonSerializer.Serialize(items);
File.WriteAllText(path, json);
```

---

## 6. UI/Rendering Issues (Avalonia/WPF)

### Pattern: UI Updates on Background Thread

**Bad:**
```csharp
// Blocks UI thread
this.Dispatcher.Invoke(() => UpdateUI());
Thread.Sleep(100);
```

**Good:**
```csharp
// Async/await
await Task.Run(() => HeavyWork());
this.Dispatcher.Invoke(() => UpdateUI());
```

### Pattern: Creating New UI Elements in Loops

**Bad:**
```csharp
// Allocates n UI elements
for (int i = 0; i < n; i++)
{
    var rect = new Rectangle();
    Canvas.Children.Add(rect);
}
```

**Good:**
```csharp
// Reuse elements or use ItemsControl
itemsControl.ItemsSource = data;
```

---

## 7. Reflection and Dynamic Calls

### Pattern: Reflection in Hot Paths

**Bad:**
```csharp
// Slow reflection calls
foreach (var obj in objects)
{
    var prop = obj.GetType().GetProperty("Name");
    var value = prop.GetValue(obj);
}
```

**Good:**
```csharp
// Cached delegates or direct access
var getter = GetType().GetProperty("Name")
              ?.GetValue;
foreach (var obj in objects)
{
    var value = getter(obj);
}
```

---

## 8. Database/ORM Issues

### Pattern: N+1 Query Problem

**Bad:**
```csharp
// First query: SELECT * FROM Orders
// Second query: SELECT * FROM OrderItems WHERE OrderId = @id (executed n times)
foreach (var order in orders)
{
    var items = db.OrderItems.Where(x => x.OrderId == order.Id).ToList();
}
```

**Good:**
```csharp
// Single query with JOIN
var result = db.Orders
    .Join(db.OrderItems, o => o.Id, i => i.OrderId, (o, i) => new { o, i })
    .Where(...)
    .ToList();
```

### Pattern: Loading Unnecessary Data

**Bad:**
```csharp
// Loads entire table into memory
var allUsers = dbContext.Users.ToList();
var activeUsers = allUsers.Where(u => u.IsActive);
```

**Good:**
```csharp
// Filters at database level
var activeUsers = dbContext.Users
    .Where(u => u.IsActive)
    .ToList();
```

---

## 9. Caching Issues

### Pattern: No Caching with Repeated Calculations

**Bad:**
```csharp
// Recalculates expensive operation
for (int i = 0; i < n; i++)
{
    var result = ExpensiveCalculation(x);
}
```

**Good:**
```csharp
// Cache result
var cache = new Dictionary<TKey, TValue>();
TValue result;
if (!cache.TryGetValue(key, out result))
{
    result = ExpensiveCalculation(x);
    cache[key] = result;
}
```

---

## 10. Locking Issues

### Pattern: Overly Broad Locking

**Bad:**
```csharp
// Locks entire method unnecessarily
public void DoWork()
{
    lock (lockObj)
    {
        foreach (var item in items)
        {
            item.Process();
        }
    }
}
```

**Good:**
```csharp
// Only lock critical section
lock (lockObj)
{
    item.Process(); // Only if item is shared
}
```

---

## References
- .NET Performance Guidelines: https://learn.microsoft.com/en-us/dotnet/core/performance/
- Roslyn Performance: https://github.com/dotnet/roslyn/blob/main/docs/compilers/Analyzer%20Performance.md
- Stack Overflow Performance: https://stackoverflow.com/performance

---


---

## 14. AI Agent Documentation

> **All AI agent troubleshooting and best practices have moved to:**
> **[agents.md](agents.md)**
>
> This section now contains only performance anti-patterns. For AI agent
guidance (tool usage, common failures, behavior checklist), please refer
to the main agents.md file.

### Quick Reference

| Issue Type | Severity | Where to Find |
|------------|----------|---------------|
| Edit toolcall failures | Critical | agents.md:14.1 |
| LINQ materialization | High | agents.md:14.6 |
| List.Contains() | High | agents.md:14.6 |
| Null warnings after fixes | Medium | agents.md:14.7 |
| Type casting | Medium | agents.md:14.11 |
| Partial method conflicts | Medium | agents.md:14.5 |

