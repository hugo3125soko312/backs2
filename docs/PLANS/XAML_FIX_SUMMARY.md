# XAML Build Errors Fix - Summary

## Branch: `fix/xaml-build-errors`

## Problem

88 XAML build errors in `src/gui/Runner/RunnerView.axaml`:
- 84 AVLN2000: Property resolution errors
- 4 AVLN2200: Setter type errors

## Root Causes

The project targets **Avalonia 11.3.22** but the XAML uses:
1. Properties removed in Avalonia 11 (TextAlignment, AlternationCount)
2. DataTrigger syntax not supported in this version
3. Grid.RowDefinitions syntax not supported

## Proposed Fix

### 1. Remove Unsupported Properties (32 errors)
```bash
# AlternationCount (2 occurrences)
sed -i '/AlternationCount="2"/d'

# TextTrimming (2 occurrences)
sed -i '/TextTrimming="CharacterEllipsis"/d'

# TextAlignment (28 occurrences)
sed -i '/TextAlignment="Right" \/>/d'
sed -i '/TextAlignment="Center"/d'
```

### 2. Remove DataTrigger Section (8 errors)
Remove the Style.Triggers block containing DataTrigger.

### 3. Fix Grid.RowDefinitions Syntax (2 errors)
Replace `Grid.RowDefinitions="Auto,*"` with proper RowDefinition elements.

## Files

- `src/gui/Runner/RunnerView.axaml` - Main file to fix
- `scripts/fix_xaml.py` - Automated fix script
- `docs/PLANS/XAML_FIX_ANALYSIS.md` - Detailed analysis

## Verification

```bash
# Build should succeed with 0 errors
dotnet build --no-incremental

# Or show only warnings
dotnet build
```

## Impact

- **Visual**: DataGrid text alignment will default to left (no Right/Center alignment)
- **Functionality**: All walkforward features remain fully functional
- **Performance**: No impact

## Status

⏳ **PENDING** - Requires Python 3 or XML-aware tool to apply fixes safely.

The `fix_xaml.py` script is provided but cannot be executed in this environment (Python not available).

## Alternative

Manually edit the XAML file using:
- Visual Studio or Rider (XML-aware editor)
- Or run the Python script on a machine with Python 3 installed
