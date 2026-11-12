$path = 'src/gui/Runner/RunnerView.axaml'
$content = Get-Content -Raw -Path $path

# 1. Remove AlternationCount="2" lines entirely
$content = $content -replace 'AlternationCount="2"\r?\n', ''

# 2. Remove TextTrimming="CharacterEllipsis" inline (preserves line structure)
$content = $content -replace 'TextTrimming="CharacterEllipsis"', ''

# 3. Remove TextAlignment="Right" / "Center" inline
$content = $content -replace 'TextAlignment="(Right|Center)"', ''

# 4. Remove <Style.Triggers> line + next 2 lines (DataTrigger + </DataTrigger>)
$lines = $content -split '\r?\n'
$out = @()
$skip = 0
foreach ($line in $lines) {
    $s = $line.Trim()
    if ($s -eq '<Style.Triggers>') {
        $skip = 3
        continue
    }
    if ($skip -gt 0) {
        $skip--
        continue
    }
    $out += $line
}
$content = $out -join "`r`n"

$content | Set-Content -Path $path -Encoding UTF8
Write-Host "Done. Lines: $($out.Count)"
