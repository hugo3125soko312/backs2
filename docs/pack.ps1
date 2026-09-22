# pack.ps1 — Bundles starting prompt + all docs + project tree into the clipboard.
#
# Usage:
#   .\docs\pack.ps1
#
# Result: the full bundle is copied to the Windows clipboard. Paste it into
# any chat/agent. No secrets are included — .env contents are excluded.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'backs2.csproj'))) {
    Write-Error "Run this from the repo root or with the repo as the working directory."
    exit 1
}

Push-Location $root
try {
    $sb = [System.Text.StringBuilder]::new()

    # --- 1. Starting prompt ---
    [void]$sb.AppendLine('=================')
    [void]$sb.AppendLine('File: docs\starting_promt.txt')
    [void]$sb.AppendLine('=================')
    [void]$sb.AppendLine((Get-Content 'docs\starting_promt.txt' -Raw))
    [void]$sb.AppendLine()

    # --- 2. All docs (everything under docs/ except the pack script itself) ---
    $docs = Get-ChildItem -Path 'docs' -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -ne 'pack.ps1' }

    foreach ($doc in $docs) {
        $rel = Resolve-Path -Relative $doc.FullName
        [void]$sb.AppendLine('=================')
        [void]$sb.AppendLine("File: $rel")
        [void]$sb.AppendLine('=================')
        [void]$sb.AppendLine((Get-Content $doc.FullName -Raw))
        [void]$sb.AppendLine()
    }

    # --- 3. Project tree ---
    [void]$sb.AppendLine('================= PROJECT STRUCTURE =================')
    Get-ChildItem -Recurse -File |
        Where-Object {
            $_.FullName -notmatch '\\(bin|obj|\.git|\.vs|\.vscode)\\' -and
            $_.Name -notlike '*.g.cs' -and
            ($_.Extension -in '.cs', '.axaml', '.csproj', '.ps1' -or $_.Name -like '.env*')
        } |
        Resolve-Path -Relative |
        ForEach-Object { [void]$sb.AppendLine($_) }

    # --- 4. Copy to clipboard ---
    $text = $sb.ToString()
    Set-Clipboard -Value $text

    $lineCount = ($text -split "`n").Count
    Write-Host "Packed $lineCount lines to clipboard." -ForegroundColor Green
    Write-Host "Paste into the chat/agent as needed." -ForegroundColor DarkGray
}
finally {
    Pop-Location
}