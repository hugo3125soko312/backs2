# AI Agent Documentation - Backs2 Project

This document consolidates all AI agent guidance, troubleshooting, and best practices for working with the backs2 codebase.

**Project Status:** Build passes (0 errors, 0 warnings). Main application runs. Test project deleted. CLI project removed.


## Critical Rules *(MUST READ - new agents)*

### NEVER Disable These
- **TreatWarningsAsErrors** - must be `true`
- **RunAnalyzersDuringBuild** - must be `true`
- These catch bugs at compile time

### ALWAYS
- Use `write` tool for file modifications (never `edit`)
- EnableDefaultCompileItems = `true` for sub-projects
- Read file before editing
- Run `dotnet build` after fixes

### Known Pitfalls
- **CommunityToolkit.Mvvm:** Async suffix conflicts with generated files
- **XML editors:** Can corrupt XAML; prefer complete file rewrite
- **JsonSerializerOptions:** `SourceGenerationContext` requires `System.Text.Json.SourceGeneration` package
- **xUnit:** Fact attribute requires xUnit package (version 2.9.0+)

### Build Note
Pre-existing AssemblyInfo conflicts exist (unrelated to this work).

## JSON Source Generation Pattern

### Overview

The project uses C# source generators for high-performance JSON serialization in `src/core/WalkForward/WalkForwardJsonContext.cs`.

### Why Source Generation?
- **Performance:** No reflection at runtime
- **AOT compatible:** Required for ComputeSharp GPU paths
- **Type safety:** Compile-time validation

### Current Usage

**File:** `src/core/WalkForward/WalkForwardJsonContext.cs`

**Pattern:**
```csharp
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    WriteIndented = false)]
[JsonSerializable(typeof(WalkForwardBatch))]
[JsonSerializable(typeof(WalkForwardWindowResult))]
internal partial class WalkForwardJsonContext : JsonSerializerContext
{
    public const int CurrentSchemaVersion = 1;
    public const int MinSupportedSchemaVersion = 1;

    public static WalkForwardBatch? LoadWithVersionValidation(string json)
    {
        var context = new WalkForwardJsonContext();
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        
        var batch = JsonSerializer.Deserialize<WalkForwardBatch>(json, options);
        
        if (batch?.SchemaVersion < MinSupportedSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported schema version: {batch?.SchemaVersion}. " +
                $"Minimum supported: {MinSupportedSchemaVersion}");
        
        return batch;
    }
}
```

### Key Characteristics

1. **NaN/Infinity handling:** Uses `JsonNumberHandling.AllowNamedFloatingPointLiterals` because WF stores NaN/Infinity when metrics are undefined
2. **Version validation:** Schema versioning prevents loading incompatible data
3. **Separate from Optimizer:** Does NOT touch OptimizationJsonContext to avoid breaking optimizer archives

### Adding New Types

1. Add `[JsonSerializable(typeof(NewType))]` attribute
2. Regenerate source: `dotnet build` (automatic)
3. Update `LoadWithVersionValidation` if new types need version checks

### Common Issues

| Error | Cause | Fix |
|## Project Structure

### Current Architecture

```
backs2/
├── backs2.csproj              # Main project (WinExe, net8.0)
├── AGENTS.md                  # AI agent documentation
├── project_oop.md             # Architecture docs
├── docs/                      # Additional documentation
│   ├── audit-report.md
│   ├── PERF_AUDIT_LLM_BUGS.md
│   └── ...
├── src/
│   ├── gui/                   # Avalonia UI
│   │   ├── MainWindow.axaml
│   │   ├── App.axaml
│   │   └── Runner/
│   │       └── RunnerView.axaml (split into partials)
│   ├── core/                  # Core logic
│   │   ├── WalkForward/
│   │   │   └── WalkForwardJsonContext.cs  # JSON source generation
│   │   ├── Optimizer/
│   │   ├── Accounts/
│   │   └── ...
│   └── compute/               # Compute backends
└── data/
    ├── cache/
    └── walkforward/
```

### RunnerViewModel Structure

Split into 7 partial files (< 300 lines each):
- `RunnerViewModel.cs` (main, 422 lines)
- `RunnerViewModel.Run.cs` (155 lines)
- `RunnerViewModel.Optimize.cs` (230 lines)
- `RunnerViewModel.WalkForward.cs` (338 lines)
- `RunnerViewModel.WalkForwardResults.cs` (179 lines)
- `RunnerViewModel.Results.cs` (170 lines)
- `RunnerViewModel.Presets.cs` (127 lines)
- `RunnerViewModel.Grid.cs` (126 lines)

### Key Patterns

1. **Mvvm pattern:** CommunityToolkit.Mvvm with RelayCommand
2. **DI container:** Microsoft.Extensions.DependencyInjection
3. **JSON persistence:** Source-generated with schema versioning
4. **Async I/O:** CancellationToken support with off-UI-thread operations


-------|-------|-----|
| `CS0117: SourceGenerationContext not found` | Missing `System.Text.Json.SourceGeneration` package | Add package reference |
| `CS8632: nullable reference types` | Missing nullable context | Enable `<Nullable>enable</Nullable>` |


---

## Table of Contents
1. [Quick Start](#quick-start)
2. [Critical Rules](#critical-rules) *(new agents MUST read this)*
2. [Common Failures](#common-failures)
3. [File Modification Guidelines](#file-modification-guidelines)
4. [Build & Verification](#build--verification)
5. [AI Agent Troubleshooting](#ai-agent-troubleshooting)
6. [Performance Anti-Patterns](#performance-anti-patterns)
7. [Project Architecture](#project-architecture)

---

## Quick Start

### For New AI Agents

**Always read section 2 (Critical Rules) before making changes.**

```bash
# 1. Check current branch and status
git status
git branch

# 2. Review recent changes
git log --oneline -10

# 3. Check project structure
ls -la src/

# 4. Review architecture docs
cat project_oop.md

# 5. Check build status
dotnet build -c Release 2>&1 | tail -3

# 6. Check performance audit
grep -A5 "## Summary" docs/audit-report.md
```

---

## Common Failures

### 1. Edit Toolcall Failures

**Symptoms:**
- `payload line has no preceding hunk header`
- `file changed between read and edit` (hash mismatch)
- Infinite retry loops on stale tags
- File corruption from partial edits

**Root Causes:**
- Edit tool requires exact 4-hex tag matching
- Each edit changes file hash, breaking subsequent edits
- Context window truncation shifts line numbers

**Fix:** ALWAYS use `write` tool instead of `edit` for file modifications:

```python
# PROPER PATTERN:
with open('path/to/file.cs', 'r') as f:
    content = f.read()

# Modify with Python
content = content.replace('old', 'new')

with open('path/to/file.cs', 'w') as f:
    f.write(content)

# Verify
bash(command="grep -n 'new' path/to/file.cs")
```

### 2. Read/Edit Race Conditions

**Symptoms:**
- `file changed between read and edit`
- Seeing stale content after edit

**Fix:** ALWAYS re-read file immediately before edit if ANY other tool modified it.

### 3. XML/Avalonia XAML Corruption

**Symptoms:**
- `AXN0001: Unable to parse "0,{Binding Y}" as a thickness`

**Fix:** Prefer complete file rewrite over string replacement for XAML:

```python
with open('file.axaml', 'w') as f:
    f.write('<UserControl ...>...</UserControl>')
```

### 4. CommunityToolkit.Mvvm Partial Method Conflicts

**Symptoms:**
- `CS0756: A partial method may not have multiple defining declarations`

**Fix:** Check generated files before adding partial methods:
```bash
find obj -name "*.g.cs" -exec grep -l "partial.*void.*On" {} \;
```

### 5. Type Casting Mistakes

**Symptoms:**
- `Cannot implicitly convert type 'int' to 'DateTime'`

**Fix:** Always check types explicitly:
```csharp
var row = new WalkForwardWindowRow(batch, w);
Console.WriteLine(nameof(row.IsStartBar) + ": " + typeof(int));
```

---

## File Modification Guidelines

### When to Use Which Tool

| Task | Tool | Example |
|------|------|---------|
| Create new file | `write` | `write(path="src/new.cs", content="...")` |
| Modify existing file | `write` + python/bash | See below |
| Simple find/replace | `bash` with sed | `sed -i 's/old/new/g' file.cs` |
| Complex multi-file edits | python script | See pattern below |
| Quick verification | `grep` | `grep -n "pattern" src/file.cs` |
| Run tests | `bash` | `dotnet test` |

### Multi-File Edit Pattern

```python
# For multiple files:
files_to_edit = {
    'src/foo.cs': ('old_pattern', 'new_pattern'),
    'src/bar.cs': ('another', 'replacement'),
}

for filepath, (pattern, replacement) in files_to_edit.items():
    with open(filepath, 'r') as f:
        content = f.read()
    
    if pattern in content:
        content = content.replace(pattern, replacement)
        with open(filepath, 'w') as f:
            f.write(content)
```

### XAML Modification Pattern

```python
# For XAML files, read-modify-write is usually safe:
with open('file.axaml', 'r') as f:
    content = f.read()

# Make your changes
content = content.replace('old', 'new')

with open('file.axaml', 'w') as f:
    f.write(content)

# Verify XAML is valid
bash(command="dotnet build -c Release 2>&1 | grep -i error")
```

---

## Build & Verification

### Standard Build Commands

```bash
# Debug build (for development)
dotnet build

# Release build (for testing/performance)
dotnet build -c Release

# Build with warnings shown
dotnet build -c Release 2>&1 | grep -E "warning|error"

# Full build output
dotnet build -c Release 2>&1 | tail -5
```

### Verification Checklist

After any code changes:

```bash
# 1. Check for compilation errors
dotnet build -c Release 2>&1 | grep "error CS"

# 2. Check for warnings  
dotnet build -c Release 2>&1 | grep "warning CS"

# 3. Run tests
dotnet test

# 4. Grep for common issues
grep -rn "ToList()" src --include="*.cs" | head -10
```

### Build Success Criteria

✅ **Passing:** 0 errors, 0 warnings, all tests pass
⚠️ **Acceptable:** 0 errors, <5 warnings
❌ **Failing:** Any errors must be fixed

---

## AI Agent Troubleshooting

### 14.1 Edit Toolcall Failures

**Symptoms:**
- `payload line has no preceding hunk header`
- `file changed between read and edit`
- Stale tags after edit

**Root Causes:**
- Edit tool requires exact 4-hex tag matching
- Hash changes after each edit
- Context drift

**Fix:** Use `write` tool exclusively.

### 14.2 Read/Edit Race Conditions

**Symptoms:**
- `file changed between read and edit`

**Fix:** Re-read before edit.

### 14.3 XML/Avalonia XAML Corruption

**Symptoms:**
- `AXN0001: Unable to parse`

**Fix:** Use complete file rewrite for XAML.

### 14.4 ScottPlot API Mismatches

**Symptoms:**
- `error CS0234: 'PlotTypes' does not exist`

**Fix:** Check actual API in use:
```bash
grep -r "using ScottPlot" src --include="*.cs"
```

### 14.5 CommunityToolkit.Mvvm Conflicts

**Symptoms:**
- `CS0756: partial method may not have multiple defining declarations`

**Fix:** Check generated files:
```bash
find obj -name "*.g.cs" -exec grep -l "partial.*void" {} \;
```

### 14.6 LINQ Materialization

**Symptoms:**
- O(n²) performance
- `Contains()` in Where clause

**Fix:** Use HashSet:
```csharp
var set = list.ToHashSet();
var filtered = items.Where(set.Contains).ToList();
```

### 14.7 Null Reference Warnings

**Symptoms:**
- `warning CS8602: Dereference of possibly null reference`

**Fix:** Check warnings after fixes:
```bash
dotnet build -c Release 2>&1 | grep "warning CS"
```

### 14.8 x:Name Generator Failures

**Symptoms:**
- `The type does not exist in the assembly`

**Fix:** Verify x:Name exists in XAML.

### 14.9 Regex Pattern Syntax

**Symptoms:**
- `error CS1513: } expected`

**Fix:** Use verbatim strings:
```csharp
var pattern = @"\(.*\)";
```

### 14.10 File Hash Mismatch

**Symptoms:**
- `[path#A1B2]` → `[path#C3D4]`

**Fix:** Use latest tag from response.

### 14.11 Type Casting

**Symptoms:**
- `Cannot implicitly convert type`

**Fix:** Check types explicitly.

### 14.12 Partial Method Duplication

**Symptoms:**
- Multiple implementations

**Fix:** Check for existing implementations.

---

## Performance Anti-Patterns

See [PERF_AUDIT_LLM_BUGS.md](PERF_AUDIT_LLM_BUGS.md) for detailed performance issues.

### Quick Reference

| Issue | Severity | Fix |
|-------|----------|-----|
| O(n²) algorithms | Critical | Use HashSet, pre-allocate |
| List.Contains() in loops | High | Convert to HashSet |
| Materialized LINQ | High | Stream with foreach |
| Brush parsing in loops | Medium | Cache brush instances |
| Boxing/unboxing | Medium | Use generic collections |
| String concatenation | Medium | Use StringBuilder |

---

## Project Architecture

### Key Files

| Path | Purpose |
|------|---------|
| `src/gui/Runner/` | Main runner UI |
| `src/core/WalkForward/` | Walk-forward optimization |
| `src/core/Computing/` | GPU/CPU compute backends |
| `src/core/Accounts/` | Account management |
| `docs/` | Documentation |

### Navigation Nodes

```csharp
// src/gui/NavigationNode.cs
new NavigationNode { Id = "runner", Title = "Runner", Content = typeof(RunnerView) },
new NavigationNode { Id = "walkforward", Title = "Walk-Forward", Content = typeof(WalkForwardTimelineView) },
new NavigationNode { Id = "montecarlo", Title = "Monte Carlo", Content = typeof(MonteCarloView) },
```

### Command Wiring

```csharp
// Common pattern in ViewModel
[RelayCommand] private void Refresh() { /* logic */ }

// In XAML
<Button Command="{Binding RefreshCommand}" Content="Refresh" />
```

---

## Working with This Project

### Agent Behavior Checklist

#### ALWAYS
1. Use `write` tool for file modifications, never `edit`
2. Read file immediately before edit if modified
3. Re-read edited file to verify
4. Run `dotnet build` after fixes
5. Check warnings after fixes

#### NEVER
1. Use `edit` tool (use `write` + bash/python)
2. Assume API types without checking
3. Remove code without checking source generators
4. Ignore warnings after "fixes"
5. Edit files just modified without re-reading

#### WHEN STUCK
1. Switch to bash/python for file operations
2. Use `grep` to verify before writing
3. Use `sed` for simple replacements
4. Use python for complex multi-file edits

### Key Patterns

#### LINQ Usage

```csharp
// BAD - materializes twice
var filtered = items.Select(x => Transform(x)).ToList();
var result = filtered.Where(x => Filter(x)).ToList();

// GOOD - single allocation
var result = items.Where(x => Filter(x))
                  .Select(x => Transform(x))
                  .ToList();
```

#### Collection Lookups

```csharp
// BAD - O(n) per lookup
if (list.Contains(item)) { ... }

// GOOD - O(1) lookup
var set = list.ToHashSet();
if (set.Contains(item)) { ... }
```

#### String Building

```csharp
// BAD - O(n²) allocations
string result = "";
for (int i = 0; i < n; i++)
{
    result += GetString(i);
}

// GOOD - O(n) allocations
var sb = new StringBuilder();
for (int i = 0; i < n; i++)
{
    sb.Append(GetString(i));
}
```

---

## References

- [Performance Audit - LLM Bugs](PERF_AUDIT_LLM_BUGS.md)
- [Audit Report](audit-report.md)
- [Project Architecture](project_oop.md)

---

## Version History

| Date | Version | Changes |
|------|---------|---------|
| 2026-11-12 | 1.0 | Initial creation |

---

*This document is maintained for AI agent consumption. Edit with care.*
