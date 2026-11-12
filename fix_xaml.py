"""Remove unsupported XAML attrs and fix resulting orphaned > lines."""
with open('src/gui/Runner/RunnerView.axaml', 'r', encoding='utf-8') as f:
    lines = f.readlines()

out = []
skip = 0

for line in lines:
    s = line.strip()
    raw = line.rstrip('\n\r')

    # Skip AlternationCount lines
    if 'AlternationCount' in s:
        continue

    # Remove TextTrimming - but keep the /> if present
    if 'TextTrimming="CharacterEllipsis"' in s:
        # Remove the attribute
        line = s.replace('TextTrimming="CharacterEllipsis"', '').rstrip()
        # If we're left with just "/>", keep it as "/>" on a new line
        if line == '/':
            line = '/>'
        line += '\n'

    # Remove TextAlignment, merge orphaned >
    elif 'TextAlignment=' in s:
        line = s.replace('TextAlignment="Right"', '').replace('TextAlignment="Center"', '').rstrip()
        # If removal left just ">", merge with previous line's attribute
        if line == '>':
            if out and out[-1].strip():
                prev = out.pop()
                prev = prev.rstrip() + ' />\n'
                out.append(prev)
            continue
        # For " />" on same line as TextAlignment, keep the />
        out.append(line + '\n')
        continue

    # Remove Style.Triggers block (4 lines)
    if s == '<Style.Triggers>':
        skip = 4
        continue
    if skip > 0:
        skip -= 1
        continue

    out.append(raw + '\n')

# Second pass: merge orphaned ">" lines with previous line
merged = []
prev_line = None
for line in out:
    stripped = line.strip()
    # If current line is just ">" or " />", merge with previous
    if stripped in ('>', ' />') and prev_line is not None:
        merged[-1] = prev_line.rstrip() + stripped + '\n'
        prev_line = None
    else:
        merged.append(line)
        prev_line = line

result = ''.join(merged)

with open('src/gui/Runner/RunnerView.axaml', 'w', encoding='utf-8') as f:
    f.write(result)

print('Done')
