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
#   - "DOCS NOT BUNDLED" section listing every omitted doc with a fenced request command
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
        '.ps1', '.sh', '.bat', '.cmd', '.xml', '.config', '.toml', '.ini', '.go'
    )

    $excludeNames = @('pack.ps1', 'starting_promt.txt')

    $coreNames = @(
        'CONTEXT_ESSENTIALS.md',
        'AGENT_RULES.md',
        'ROADMAP.md',
        'LONGWORK_FORMAT.md',
        'PROTOCOL.txt',
        'watch_downloads.go'
    )
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

    $allDocs = @(
        Get-ChildItem -Path 'docs' -Recurse -File |
            Where-Object {
                $_.FullName -notmatch '\\(bin|obj)\\' -and
                $_.Name -notin $excludeNames -and
                ($textExtensions -contains $_.Extension.ToLower())
            }
    )

    # Extra files outside docs/ that belong in the bundle context.
    # Currently: the BDS exec-bridge protocol, which teaches the model
    # the fence tags used by the browser userscript.
    $externalIncludePaths = @('tools\exec-bridge\PROTOCOL.txt', 'tools\exec-bridge\TOOLCALLS.txt')
    foreach ($extPath in $externalIncludePaths) {
        if (Test-Path $extPath) { $allDocs += Get-Item $extPath }
    }

    $allDocs = @($allDocs | Sort-Object FullName)

    $docs = if ($Full) { $allDocs }
            else        { @($allDocs | Where-Object { $_.Name -in $includeNames }) }

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
    [void]$sb.AppendLine('  7. If the request needs project context, summarize what you read in <=5 bullets.')
    [void]$sb.AppendLine('  8. Do not analyze this bundle unless the request requires it. Keep initial')
    [void]$sb.AppendLine('     reasoning short; respond to the user directly.')
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
    # Each request command is emitted inside a ```powershell fenced block so
    # the AI reading this bundle sees the correct pattern (see AGENT_RULES §1
    # and §3). Do not switch to indented text or inline backticks here — that
    # teaches the wrong format and the AI will mirror it in its own output.
    [void]$sb.AppendLine('================= DOCS NOT BUNDLED (request on demand) =================')
    if ($Full -or $omittedDocs.Count -eq 0) {
        [void]$sb.AppendLine('Every text doc under docs\ is inlined above.')
    }
    else {
        [void]$sb.AppendLine('These files are on disk but were not inlined. Request one at a time')
        [void]$sb.AppendLine('when the task touches its subject — do not request them all up front.')
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('Every request is a `Get-Content <path> | Set-Clipboard` command inside a')
        [void]$sb.AppendLine('```powershell fenced block, per AGENT_RULES §1 and §3. Mimic the fence.')
        [void]$sb.AppendLine()
        foreach ($doc in $omittedDocs) {
            $rel = Resolve-Path -Relative $doc.FullName
            [void]$sb.AppendLine(('**{0}**' -f $rel))
            [void]$sb.AppendLine()
            [void]$sb.AppendLine('```powershell')
            [void]$sb.AppendLine(('Get-Content {0} | Set-Clipboard' -f $rel))
            [void]$sb.AppendLine('```')
            [void]$sb.AppendLine()
        }
    }
    [void]$sb.AppendLine()

    # --- 5. Final reminder ---
    [void]$sb.AppendLine('================= FINAL REMINDER / CRITICAL RULES =================')
    [void]$sb.AppendLine('Before answering, confirm:')
    [void]$sb.AppendLine('  - You have read docs\starting_promt.txt and can list its rules.')
    [void]$sb.AppendLine('  - You have read docs\CONTEXT_ESSENTIALS.md — the distilled architecture.')
    [void]$sb.AppendLine('  - You will follow the Output Contract in docs\AGENT_RULES.md.')
    [void]$sb.AppendLine('  - Every command you emit is inside a ```powershell fenced block, including')
    [void]$sb.AppendLine('    every Get-Content ... | Set-Clipboard request.')
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

# --- 7. Rebuild and launch the exec-bridge daemon + Q&A terminal ---
Write-Host
Write-Host 'Press ENTER to rebuild and launch the exec-bridge daemon + Q&A terminal, or type anything and press ENTER to exit:' -ForegroundColor Cyan
$response = Read-Host
if ([string]::IsNullOrWhiteSpace($response)) {
    $bridgeDir = Join-Path $root 'tools\exec-bridge'
    $exePath   = Join-Path $bridgeDir 'exec_bridge.exe'

    if (-not (Test-Path $bridgeDir)) {
        Write-Host "Bridge directory not found: $bridgeDir" -ForegroundColor Red
    } else {
        $running = Get-Process exec_bridge -ErrorAction SilentlyContinue
        if ($running) {
            Write-Host ("Stopping running exec_bridge (PID {0})..." -f (($running | ForEach-Object { $_.Id }) -join ', ')) -ForegroundColor Yellow
            $running | Stop-Process -Force
            Start-Sleep -Milliseconds 400
        }

        Write-Host 'Rebuilding exec_bridge.exe from source...' -ForegroundColor Yellow
        Push-Location $bridgeDir
        try {
            go build -o exec_bridge.exe
            if ($LASTEXITCODE -ne 0) { Write-Host "go build exited with code $LASTEXITCODE" -ForegroundColor Red }
        } finally { Pop-Location }

        if (Test-Path $exePath) {
            $shellExe = if (Get-Command pwsh.exe -ErrorAction SilentlyContinue) { 'pwsh.exe' } else { 'powershell.exe' }

            Start-Process -FilePath $shellExe -ArgumentList '-NoExit', '-NoProfile', '-Command', "& '$exePath'" | Out-Null
            Write-Host '  daemon (logging) launched' -ForegroundColor Green

            Start-Sleep -Milliseconds 800

            Start-Process -FilePath $shellExe -ArgumentList '-NoExit', '-NoProfile', '-Command', "& '$exePath' -qterm" | Out-Null
            Write-Host '  Q&A terminal launched' -ForegroundColor Green
        } else {
            Write-Host 'Build failed; daemon not launched.' -ForegroundColor Red
        }
    }
}