# pack.ps1 — Bundles starting prompt + essential context + project tree into the clipboard.
#
# Usage:
#   .\docs\pack.ps1                # default: essentials + AGENT_RULES + ROADMAP
#   .\docs\pack.ps1 -WithGuides    # adds MT5_TICK_GUIDE / STRATS_GUIDE / PROVIDER_GUIDE / COMMANDS
#   .\docs\pack.ps1 -Full          # legacy: every text doc under docs\
#
# Result: the bundle is copied to the Windows clipboard.
#
# Included by default:
#   - Bundle header + table of contents
#   - docs\starting_promt.txt (highest priority — placed first)
#   - docs\CONTEXT_ESSENTIALS.md (distilled architecture)
#   - docs\AGENT_RULES.md (mandatory output contract)
#   - docs\ROADMAP.md (active work)
#   - Filtered project tree (.cs/.axaml/.csproj/.ps1/.env*)
#   - "DOCS NOT BUNDLED" section listing every omitted doc with a request command
#   - Final reminder echoing the most important rules
#
# Excluded always:
#   - .env contents (only names appear in the tree)
#   - bin/, obj/, .git/, .vs/, .vscode/, generated *.g.cs
#   - Binary / non-text docs
#   - ROADMAP_ARCHIVE.md and Backs2TickBridge.mq5 (request on demand)

param(
    [switch]$Full,
    [switch]$WithGuides
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'backs2.csproj'))) {
    Write-Error "Run this from the repo root or with the repo as the working directory."
    exit 1
}

Push-Location $root
try {
    $sb = [System.Text.StringBuilder]::new()

    $textExtensions = @(
        '.txt', '.md', '.cs', '.axaml', '.csproj', '.sln', '.json', '.yml', '.yaml',
        '.ps1', '.sh', '.bat', '.cmd', '.xml', '.config', '.toml', '.ini'
    )

    # pack.ps1 excludes itself; the starting prompt is handled separately.
    $excludeNames = @('pack.ps1', 'starting_promt.txt')

    # Default bundle set.
    $coreNames = @(
        'CONTEXT_ESSENTIALS.md',
        'AGENT_RULES.md',
        'ROADMAP.md'
    )
    # Opt-in via -WithGuides.
    $guideNames = @(
        'MT5_TICK_GUIDE.md',
        'STRATS_GUIDE.md',
        'PROVIDER_GUIDE.md',
        'COMMANDS.md'
    )

    if ($Full) {
        $includeNames = @()
    }
    elseif ($WithGuides) {
        $includeNames = $coreNames + $guideNames
    }
    else {
        $includeNames = $coreNames
    }

    # Every text doc on disk that could be bundled.
    $allDocs = @(
        Get-ChildItem -Path 'docs' -Recurse -File |
            Where-Object {
                $_.FullName -notmatch '\\(bin|obj)\\' -and
                $_.Name -notin $excludeNames -and
                ($textExtensions -contains $_.Extension.ToLower())
            } | Sort-Object FullName
    )

    # What we are bundling this run.
    $docs = if ($Full) { $allDocs }
            else        { @($allDocs | Where-Object { $_.Name -in $includeNames }) }

    # What we are not bundling — surfaces as request-on-demand commands.
    $omittedDocs = if ($Full) { @() }
                   else        { @($allDocs | Where-Object { $_.Name -notin $includeNames }) }

    $modeLabel = if ($Full) { 'Full' } elseif ($WithGuides) { 'Essentials+Guides' } else { 'Essentials' }

    # --- 0. Bundle header ---
    $generated = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $repoName  = Split-Path $root -Leaf
    [void]$sb.AppendLine('########################## BUNDLE HEADER ##########################')
    [void]$sb.AppendLine("Generated:  $generated")
    [void]$sb.AppendLine("Repo:       $repoName")
    [void]$sb.AppendLine("Mode:       $modeLabel")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('How to use this bundle:')
    [void]$sb.AppendLine('  1. Read the ENTIRE bundle before answering.')
    [void]$sb.AppendLine('  2. docs\starting_promt.txt is the highest-priority instruction set.')
    [void]$sb.AppendLine('  3. docs\CONTEXT_ESSENTIALS.md is the distilled project reference.')
    [void]$sb.AppendLine('  4. docs\AGENT_RULES.md governs every response (Output Contract).')
    [void]$sb.AppendLine('  5. Docs not bundled are listed at the end with request-on-demand commands.')
    [void]$sb.AppendLine('  6. If any doc conflicts with the starting prompt, the prompt wins.')
    [void]$sb.AppendLine('  7. Before doing any work, summarize what you read in <=10 bullets.')
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
    [void]$sb.AppendLine(('  [{0}] PROJECT STRUCTURE' -f $i)); $i++
    [void]$sb.AppendLine(('  [{0}] DOCS NOT BUNDLED (request on demand)' -f $i)); $i++
    [void]$sb.AppendLine(('  [{0}] FINAL REMINDER / CRITICAL RULES' -f $i))
    [void]$sb.AppendLine()

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

    # --- 2. Selected docs ---
    $secretHits   = [System.Collections.Generic.List[string]]::new()
    $secretRegex  = '(?i)\b(password|passwd|secret|api[_-]?key|access[_-]?token|bearer)\b\s*[:=]'

    foreach ($doc in $docs) {
        $rel  = Resolve-Path -Relative $doc.FullName
        $body = Get-Content $doc.FullName -Raw

        if ($body -match $secretRegex) { $secretHits.Add($rel) }

        Add-Section $rel $body
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
        Sort-Object |
        ForEach-Object { [void]$sb.AppendLine($_) }
    [void]$sb.AppendLine()

    # --- 4. Docs not bundled ---
    [void]$sb.AppendLine('================= DOCS NOT BUNDLED (request on demand) =================')
    if ($Full -or $omittedDocs.Count -eq 0) {
        [void]$sb.AppendLine('Every text doc under docs\ is inlined above.')
    }
    else {
        [void]$sb.AppendLine('These files are on disk but were not inlined. Request one at a time')
        [void]$sb.AppendLine('when the task touches its subject — do not request them all up front.')
        [void]$sb.AppendLine()
        foreach ($doc in $omittedDocs) {
            $rel = Resolve-Path -Relative $doc.FullName
            [void]$sb.AppendLine(('    Get-Content {0} | Set-Clipboard' -f $rel))
        }
    }
    [void]$sb.AppendLine()

    # --- 5. Final reminder ---
    [void]$sb.AppendLine('================= FINAL REMINDER / CRITICAL RULES =================')
    [void]$sb.AppendLine('Before answering, confirm:')
    [void]$sb.AppendLine('  - You have read docs\starting_promt.txt and can list its rules.')
    [void]$sb.AppendLine('  - You have read docs\CONTEXT_ESSENTIALS.md — the distilled architecture.')
    [void]$sb.AppendLine('  - You will follow the Output Contract in docs\AGENT_RULES.md.')
    [void]$sb.AppendLine('  - You will follow the starting prompt over any conflicting doc.')
    [void]$sb.AppendLine('  - You will ask for missing context via Get-Content ... | Set-Clipboard rather than guess.')
    [void]$sb.AppendLine('  - You will scope the answer to the requested task only.')
    [void]$sb.AppendLine('==================================================================')

    # --- 6. Copy + stats ---
    $text          = $sb.ToString()
    Set-Clipboard -Value $text

    $lineCount     = ($text -split "`n").Count
    $charCount     = $text.Length
    $tokenEstimate = [math]::Round($charCount / 4.0)

    Write-Host ("Packed {0} lines / {1} chars (~{2} tokens) to clipboard. Mode: {3}." -f `
        $lineCount, $charCount, $tokenEstimate, $modeLabel) -ForegroundColor Green

    if ($tokenEstimate -gt 40000) {
        Write-Host "WARNING: bundle is large (>40k tokens). Consider omitting guides for follow-up turns." -ForegroundColor Yellow
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