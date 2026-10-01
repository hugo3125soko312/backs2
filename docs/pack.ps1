# pack.ps1 — Bundles starting prompt + all docs + project tree into the clipboard.
#
# Usage:
#   .\docs\pack.ps1
#
# Result: the full bundle is copied to the Windows clipboard.
#
# Included:
#   - Bundle header + table of contents
#   - docs\starting_promt.txt (highest priority — placed first)
#   - All text docs under docs\ (sorted, filtered)
#   - Placeholder for ROADMAP_ARCHIVE.md
#   - Filtered project tree (.cs/.axaml/.csproj/.ps1/.env*)
#   - Final reminder echoing the most important rules
#
# Excluded:
#   - .env contents (only names appear in the tree)
#   - bin/, obj/, .git/, .vs/, .vscode/, generated *.g.cs
#   - Binary/non-text docs
#   - ROADMAP_ARCHIVE.md (placeholder only)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'backs2.csproj'))) {
    Write-Error "Run this from the repo root or with the repo as the working directory."
    exit 1
}

Push-Location $root
try {
    $sb = [System.Text.StringBuilder]::new()

    # Text-like extensions considered safe to inline.
    $textExtensions = @(
        '.txt', '.md', '.cs', '.axaml', '.csproj', '.sln', '.json', '.yml', '.yaml',
        '.ps1', '.sh', '.bat', '.cmd', '.xml', '.config', '.toml', '.ini'
    )

    # Files we never inline as sections (pack.ps1 itself, the starting prompt
    # which is handled separately, and the archive which gets a placeholder).
    $excludeNames = @('pack.ps1', 'starting_promt.txt', 'ROADMAP_ARCHIVE.md')

    # Collect docs up front so we can build a deterministic TOC.
    $docs = @(
        Get-ChildItem -Path 'docs' -Recurse -File |
            Where-Object {
                $_.FullName -notmatch '\\(bin|obj)\\' -and
                $_.Name -notin $excludeNames -and
                ($textExtensions -contains $_.Extension.ToLower())
            } | Sort-Object FullName
    )

    # --- 0. Bundle header ---
    $generated = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $repoName  = Split-Path $root -Leaf
    [void]$sb.AppendLine('########################## BUNDLE HEADER ##########################')
    [void]$sb.AppendLine("Generated:  $generated")
    [void]$sb.AppendLine("Repo:       $repoName")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('How to use this bundle:')
    [void]$sb.AppendLine('  1. Read the ENTIRE bundle before answering.')
    [void]$sb.AppendLine('  2. docs\starting_promt.txt is the highest-priority instruction set.')
    [void]$sb.AppendLine('  3. If any doc conflicts with the starting prompt, the prompt wins.')
    [void]$sb.AppendLine('  4. Before doing any work, summarize what you read in <=10 bullets.')
    [void]$sb.AppendLine('###################################################################')
    [void]$sb.AppendLine()

    # --- 0b. Table of contents ---
    [void]$sb.AppendLine('================= TABLE OF CONTENTS =================')
    $i = 1
    [void]$sb.AppendLine(('  [{0}] docs\starting_promt.txt  (READ FIRST)' -f $i)); $i++
    foreach ($doc in $docs) {
        $rel = Resolve-Path -Relative $doc.FullName
        [void]$sb.AppendLine(('  [{0}] {1}' -f $i, $rel)); $i++
    }
    [void]$sb.AppendLine(('  [{0}] docs\ROADMAP_ARCHIVE.md  (placeholder)' -f $i)); $i++
    [void]$sb.AppendLine(('  [{0}] PROJECT STRUCTURE' -f $i)); $i++
    [void]$sb.AppendLine(('  [{0}] FINAL REMINDER / CRITICAL RULES' -f $i))
    [void]$sb.AppendLine()

    # Helper: append a clearly delimited section.
    function Add-Section {
        param([string]$Label, [string]$Body)
        [void]$sb.AppendLine('=================')
        [void]$sb.AppendLine("File: $Label")
        [void]$sb.AppendLine('=================')
        [void]$sb.AppendLine($Body)
        [void]$sb.AppendLine()
    }

    # --- 1. Starting prompt ---
    Add-Section 'docs\starting_promt.txt' (Get-Content 'docs\starting_promt.txt' -Raw)

    # --- 2. Docs ---
    $secretHits   = [System.Collections.Generic.List[string]]::new()
    $secretRegex  = '(?i)\b(password|passwd|secret|api[_-]?key|access[_-]?token|bearer)\b\s*[:=]'

    foreach ($doc in $docs) {
        $rel  = Resolve-Path -Relative $doc.FullName
        $body = Get-Content $doc.FullName -Raw

        if ($body -match $secretRegex) { $secretHits.Add($rel) }

        Add-Section $rel $body
    }

    # --- 2b. ROADMAP_ARCHIVE placeholder ---
    $archiveMsg = @'
docs\ROADMAP_ARCHIVE.md contains all completed features and phases.
It is not bundled here because it is large and changes rarely.
Request it by running this from the project root:

    Get-Content docs\ROADMAP_ARCHIVE.md | Set-Clipboard
'@
    Add-Section 'docs\ROADMAP_ARCHIVE.md' $archiveMsg

    # --- 3. Project tree ---
    [void]$sb.AppendLine('================= PROJECT STRUCTURE =================')
    Get-ChildItem -Recurse -File |
        Where-Object {
            $_.FullName -notmatch '\\(bin|obj|\.git|\.vs|\.vscode)\\' -and
            $_.Name -notlike '*.g.cs' -and
            ($_.Extension -in '.cs', '.axaml', '.csproj', '.ps1' -or $_.Name -like '.env*')
        } |
        Resolve-Path -Relative |
        Sort-Object |
        ForEach-Object { [void]$sb.AppendLine($_) }
    [void]$sb.AppendLine()

    # --- 4. Final reminder (counteracts "lost in the middle") ---
    [void]$sb.AppendLine('================= FINAL REMINDER / CRITICAL RULES =================')
    [void]$sb.AppendLine('Before answering, confirm:')
    [void]$sb.AppendLine('  - You have read docs\starting_promt.txt and can list its rules.')
    [void]$sb.AppendLine('  - You will follow the starting prompt over any conflicting doc.')
    [void]$sb.AppendLine('  - You will ask for missing context (e.g. ROADMAP_ARCHIVE.md) rather than guess.')
    [void]$sb.AppendLine('  - You will scope the answer to the requested task only.')
    [void]$sb.AppendLine('==================================================================')

    # --- 5. Copy + stats ---
    $text          = $sb.ToString()
    Set-Clipboard -Value $text

    $lineCount     = ($text -split "`n").Count
    $charCount     = $text.Length
    $tokenEstimate = [math]::Round($charCount / 4.0)

    Write-Host ("Packed {0} lines / {1} chars (~{2} tokens) to clipboard." -f `
        $lineCount, $charCount, $tokenEstimate) -ForegroundColor Green

    if ($tokenEstimate -gt 60000) {
        Write-Host "WARNING: bundle is large (>60k tokens). Consider trimming docs for follow-up turns." -ForegroundColor Yellow
    }
    if ($secretHits.Count -gt 0) {
        Write-Host "WARNING: possible secrets detected in:" -ForegroundColor Yellow
        foreach ($h in $secretHits) { Write-Host "  - $h" -ForegroundColor Yellow }
        Write-Host "Review before pasting into any external service." -ForegroundColor Yellow
    }

    Write-Host "Paste into the chat/agent as needed." -ForegroundColor DarkGray
}
finally {
    Pop-Location
}