# XAML Build Errors

## Status: PENDING FIX

## Summary

The walkforward-ui-wiring branch has pre-existing XAML build errors that existed before the walkforward UI changes. These are DataGrid styling issues that need to be fixed.

## Errors

### 1. DataTrigger Not Resolved (Line 432)
**Error**: `Unable to resolve type DataTrigger from namespace https://github.com/avaloniaui`

**Location**: `src/gui/Runner/RunnerView.axaml` line 432

**Code**:
```xml
<DataTrigger Binding="{Binding McRan}" Value="True">
```

**Issue**: DataTrigger needs proper namespace or may not be supported in this Avalonia version for DataGridCell styling.

**Fix Options**:
- Use `Interactivity.Triggers` instead
- Use a `ValueConverter` on the Foreground property
- Remove conditional styling

### 2. DataGrid Properties Not Found

#### AlternationCount (Lines 381, 506)
**Error**: `Unable to resolve suitable regular or attached property AlternationCount on type Avalonia.Controls.DataGrid`

**Fix**: Remove `AlternationCount` property (not supported in Avalonia DataGrid)

#### TextAlignment (Lines 399-454, 515-588)
**Error**: `Unable to resolve suitable regular or attached property TextAlignment on type Avalonia.Controls.DataGridTextColumn`

**Fix Options**:
- Use `HorizontalAlignment` property instead
- Remove text alignment (use defaults)

#### Style/Setter Issues (Lines 429-433)
**Error**: `Could not determine target type of Setter`

**Fix**: Ensure Style TargetType is properly specified

## Pre-existing Status

These errors existed even before walkforward UI changes:
- Original XAML: 358 lines
- After walkforward additions: 606 lines
- Build errors: 44 total

## Impact

- Build fails with 44 XAML errors
- Errors are all in DataGrid styling sections
- Core functionality is not affected
- UI renders but with incorrect DataGrid styling

## Verification

```bash
git log --oneline --all -- src/gui/Runner/RunnerView.axaml
# Shows walkforward commits on top of original

dotnet build
# Fails with 44 XAML errors
```

## Next Steps

1. Fix DataGrid styling issues
2. Remove or replace DataTrigger usage
3. Remove unsupported properties (AlternationCount, TextAlignment)
4. Verify build passes
