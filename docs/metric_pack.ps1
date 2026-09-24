# docs\metric_pack.ps1
# Extracts every registered metric's Name | Lag | Category | Description
# from src\core\Metrics\MetricDefinitions.cs and copies the result to the clipboard.
# Depends on the property order Name -> Lag -> Category -> Description on consecutive lines.

$source = Join-Path $PSScriptRoot '..\src\core\Metrics\MetricDefinitions.cs'

if (-not (Test-Path $source)) {
    Write-Host "MetricDefinitions.cs not found at $source"
    exit 1
}

$lines = Get-Content $source
$result = @()

for ($i = 0; $i -lt $lines.Count; $i++) {
    $m = [regex]::Match($lines[$i], 'public override string Name => "(.*?)";')
    if ($m.Success) {
        $name = $m.Groups[1].Value
        $lag  = ([regex]::Match($lines[$i + 1], 'MetricLag\.(\w+)')).Groups[1].Value
        $cat  = ([regex]::Match($lines[$i + 2], 'MetricCategory\.(\w+)')).Groups[1].Value
        $desc = ([regex]::Match($lines[$i + 3], '=> "(.*?)";')).Groups[1].Value
        $result += "$name | $lag | $cat | $desc"
    }
}

$result | Set-Clipboard
Write-Host "Packed $($result.Count) metrics to clipboard."
Write-Host "Paste into the chat/agent as needed."