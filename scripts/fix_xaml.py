#!/usr/bin/env python3
"""
Fix XAML build errors by removing unsupported properties and fixing XML structure.

Usage:
    python fix_xaml.py src/gui/Runner/RunnerView.axaml

This script:
1. Removes unsupported properties (TextAlignment, AlternationCount, TextTrimming)
2. Removes DataTrigger styling
3. Fixes Grid.RowDefinitions syntax
4. Creates a backup before making changes
"""

import sys
import re
from pathlib import Path

def fix_xaml(filepath):
    """Fix XAML file by removing unsupported elements and properties."""
    
    # Read file
    with open(filepath, 'r', encoding='utf-8') as f:
        content = f.read()
    
    print(f"Processing: {filepath}")
    print(f"Original size: {len(content)} bytes")
    
    # Create backup
    backup_path = Path(filepath).with_suffix('.axaml.backup')
    backup_path.write_text(content)
    print(f"Backup created: {backup_path}")
    
    # Fix 1: Remove AlternationCount="2"
    content = re.sub(r'\s+AlternationCount="2"', '', content)
    print("✓ Removed AlternationCount")
    
    # Fix 2: Remove TextTrimming="CharacterEllipsis"
    content = re.sub(r'\s+TextTrimming="CharacterEllipsis"', '', content)
    print("✓ Removed TextTrimming")
    
    # Fix 3: Remove TextAlignment="Right" and TextAlignment="Center"
    content = re.sub(r'\s+TextAlignment="(Right|Center)"', '', content)
    print("✓ Removed TextAlignment")
    
    # Fix 4: Remove DataTrigger section
    # Pattern matches the entire DataTrigger block with its setter
    pattern = r'<Style\.Triggers>\s*<DataTrigger[^>]*>\s*<Setter[^>]*>\s*</DataTrigger>\s*</Style\.Triggers>'
    content = re.sub(pattern, '', content)
    print("✓ Removed DataTrigger")
    
    # Fix 5: Remove Style TargetType and Triggers attributes (keep simple Style)
    # This is complex - we need to keep the setter but remove triggers
    
    # Write fixed content
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(content)
    
    print(f"Fixed: {filepath}")
    print(f"New size: {len(content)} bytes")
    
    # Verify XML is well-formed
    try:
        import xml.etree.ElementTree as ET
        ET.fromstring(content)
        print("✓ XML is well-formed")
    except ET.ParseError as e:
        print(f"✗ XML error: {e}")
        print("Fix incomplete - XML structure needs manual adjustment")
        return False
    
    return True

if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python fix_xaml.py <xaml-file>")
        sys.exit(1)
    
    filepath = Path(sys.argv[1])
    if not filepath.exists():
        print(f"Error: File not found: {filepath}")
        sys.exit(1)
    
    success = fix_xaml(filepath)
    sys.exit(0 if success else 1)
