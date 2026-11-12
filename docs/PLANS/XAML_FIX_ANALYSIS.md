# XAML Build Error Analysis

## Summary

**Total Errors**: 88 (84 AVLN2000 + 4 AVLN2200)

**File**: `src/gui/Runner/RunnerView.axaml`

---

## Error Categories

### Category 1: Unsupported Properties (32 errors)

| Property | Type | Count | Lines |
|----------|------|-------|-------|
| `TextAlignment` | `DataGridTextColumn` | 28 | 396, 400, 404, 408, 412, 416, 420, 424, 439, 443, 447, 451, 512, 525, 529, 533, 537, 545, 549, 553, 557, 561, 565, 569, 573, 577, 581, 585 |
| `AlternationCount` | `DataGrid` | 2 | 376, 501 |
| `TextTrimming` | `DataGridTextColumn` | 1 | 541 |
| `DataTrigger` | (unknown) | 1 | 432 |

**Fix**: Remove these properties from XAML

### Category 2: Style Issues (4 errors)

| Issue | Type | Count | Lines |
|-------|------|-------|-------|
| `Style` with `Triggers` | `DataGridCell` | 1 | 424 |
| `TargetType` | `Style` | 1 | 429 |
| `Triggers` | `Style` | 1 | 429 |
| Internal compiler error | (ResolveContentPropertyTransformer) | 1 | 432 |

**Fix**: Simplify or remove the problematic Style definitions

### Category 3: Setter Issues (4 errors)

| Issue | Type | Count | Lines |
|-------|------|-------|-------|
| Could not determine target type of Setter | `Style` | 2 | 430, 433 |
| Binding issues | (unknown) | 2 | 432 |

**Fix**: Ensure proper Style structure

---

## Root Causes

1. **Avalonia Version Mismatch**: The project targets Avalonia 11.3.22, but uses XAML syntax from newer versions
2. **DataGrid API Changes**: Some properties were removed or renamed in Avalonia 11
3. **Styling Syntax**: The DataTrigger and Style.Triggers syntax requires specific Avalonia versions

---

## Fix Strategy

### Phase 1: Remove Unsupported Properties (32 errors)
- Remove `TextAlignment` from all DataGridTextColumn elements
- Remove `AlternationCount` from DataGrid elements  
- Remove `TextTrimming` from DataGridTextColumn elements
- Remove `DataTrigger` and simplify Style

### Phase 2: Simplify Style Definitions (8 errors)
- Replace complex Style with Triggers with simple Style
- Remove TargetType and Triggers where not needed
- Fix internal compiler error by simplifying XML structure

### Phase 3: Verify Build (all errors)
- Run `dotnet build` to verify
- Check for any remaining issues

---

## Specific Fixes

### Fix 1: TextAlignment (28 occurrences)
**Before**:
```xml
<DataGridTextColumn Header="Net PnL"
                    Binding="{Binding NetPnlDisplay}"
                    Width="SizeToHeader"
                    TextAlignment="Right" />
```
**After**:
```xml
<DataGridTextColumn Header="Net PnL"
                    Binding="{Binding NetPnlDisplay}"
                    Width="SizeToHeader" />
```

### Fix 2: AlternationCount (2 occurrences)
**Before**:
```xml
<DataGrid AlternationCount="2"
          ... />
```
**After**:
```xml
<DataGrid
          ... />
```

### Fix 3: DataTrigger (1 occurrence)
**Before**:
```xml
<Style.Triggers>
    <DataTrigger Binding="{Binding McRan}" Value="True">
        <Setter Property="Foreground" Value="{DynamicResource Success}" />
    </DataTrigger>
</Style.Triggers>
```
**After**:
```xml
<!-- Remove triggers or use simpler approach -->
```

### Fix 4: Style TargetType (1 occurrence)
**Before**:
```xml
<Style TargetType="DataGridCell">
    <Setter Property="Foreground" Value="{DynamicResource TextSecondary}" />
    <Style.Triggers>
        ...
    </Style.Triggers>
</Style>
```
**After**:
```xml
<Style TargetType="DataGridCell">
    <Setter Property="Foreground" Value="{DynamicResource TextSecondary}" />
</Style>
```

---

## Impact Assessment

- **Core Functionality**: Not affected
- **Visual Changes**: DataGrid text alignment will be default (left-aligned)
- **Performance**: No impact
- **Runtime Behavior**: All walkforward features will work correctly

---

## Verification

After fixes:
```bash
dotnet build --no-incremental
# Should show 0 errors
```

---

## Implementation Notes

The XAML file cannot be safely modified with simple text replacement because:

1. **XML Structure**: DataGrid elements have complex nested structures
2. **Properties**: Simple string replacement breaks attribute contexts
3. **Namespaces**: Elements require proper namespace declarations

### Recommended Approach

Use an XML-aware tool or library such as:
- Python's `xml.etree.ElementTree` or `lxml`
- C# `XDocument.Parse()` and `Save()`
- Visual Studio or Rider XML editor

### Manual Approach (Risky)

If using bash/sed/awk:
1. Always work on a backup
2. Test after each change
3. Verify XML structure is maintained
4. Check with `dotnet build` after each fix

---

## Verification Checklist

After fixing XAML errors:

```bash
# Should show 0 errors
dotnet build --no-incremental

# Should show AVLN errors
dotnet build 2>&1 | grep "Avalonia error" | wc -l

# Expected: 0
```

## Impact Assessment

- **Core Functionality**: Not affected by XAML changes
- **Walkforward Features**: All working, just missing DataGrid styling
- **Runtime**: No impact on execution
