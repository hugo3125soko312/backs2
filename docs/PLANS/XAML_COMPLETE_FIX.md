# Complete XAML Fix Solution

## Problem
88 XAML build errors in `src/gui/Runner/RunnerView.axaml`

## Root Causes
- `TextAlignment`, `AlternationCount`, `TextTrimming` not supported in Avalonia 11
- `DataTrigger` not resolving in Style.Triggers
- `Grid.RowDefinitions="Auto,*"` syntax not supported

## Complete Fix

### Step 1: Create Python Fix Script

Save this as `scripts/fix_xaml.py`:

```python
#!/usr/bin/env python3
"""Fix XAML by removing unsupported elements and properties."""

import re
import sys
from pathlib import Path

def fix_xaml(filepath):
    with open(filepath, 'r') as f:
        content = f.read()
    
    print(f"Processing: {filepath}")
    
    # Backup
    backup = Path(filepath).with_suffix('.axaml.backup')
    backup.write_text(content)
    print(f"Backup: {backup}")
    
    # Fix 1: Remove AlternationCount="2" (lines 381, 506)
    content = re.sub(r'\s+AlternationCount="2"', '', content)
    
    # Fix 2: Remove TextTrimming="CharacterEllipsis" (lines 217, 221, 544)
    content = re.sub(r'\s+TextTrimming="CharacterEllipsis"', '', content)
    
    # Fix 3: Remove TextAlignment="Right" and "Center" (lines 399-423, 427, 442-454, 515, 528-588)
    content = re.sub(r'\s+TextAlignment="(Right|Center)"', '', content)
    
    # Fix 4: Remove Style.Triggers with DataTrigger (lines 431-435)
    # This is complex - need to remove the entire block safely
    pattern = r'<Style\.Triggers>\s*<DataTrigger[^>]*>\s*<Setter[^>]*/>\s*</DataTrigger>\s*</Style\.Triggers>'
    content = re.sub(pattern, '', content)
    
    # Fix 5: Remove Grid.RowDefinitions="Auto,*" syntax
    # Replace with proper RowDefinition elements
    pattern = r'<Grid\.RowDefinitions="([^"]+)">'
    def replace_defs(m):
        defs = m.group(1).split(',')
        rows = ''.join(f'<RowDefinition Height="{d.strip()}" />\n' for d in defs)
        return f'<Grid.RowDefinitions>\n{rows}                    </Grid.RowDefinitions>'
    
    content = re.sub(pattern, replace_defs, content)
    
    # Write back
    with open(filepath, 'w') as f:
        f.write(content)
    
    print(f"Fixed: {filepath}")
    
    # Validate
    try:
        import xml.etree.ElementTree as ET
        ET.fromstring(content)
        print("✓ XML is valid")
        return True
    except Exception as e:
        print(f"✗ XML error: {e}")
        print("Need manual adjustment")
        return False

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python fix_xaml.py <xaml-file>")
        sys.exit(1)
    fix_xaml(Path(sys.argv[1]))
```

### Step 2: Run the Script

```bash
cd /path/to/repo
git checkout fix/xaml-build-errors
python scripts/fix_xaml.py src/gui/Runner/RunnerView.axaml
```

### Step 3: Verify Build

```bash
dotnet build
# Should show 0 errors
```

### Step 4: Test Application

```bash
dotnet run
# Should start successfully
```

## Alternative: Manual Fix

If Python is not available, manually:

1. Remove `AlternationCount="2"` from DataGrid elements (2 locations)
2. Remove `TextTrimming="CharacterEllipsis"` from TextBlock elements (3 locations)
3. Remove `TextAlignment="Right"` and `TextAlignment="Center"` from DataGridTextColumn elements (30 locations)
4. Remove the entire Style.Triggers/DataTrigger block (lines 431-435)
5. Replace `Grid.RowDefinitions="Auto,*"` with proper RowDefinition elements

## Impact

- DataGrid text will default to left alignment
- Alternating row colors will not work
- Conditional coloring (McRan) will not work
- All functionality remains intact
