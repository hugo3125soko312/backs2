# pack.ps1 - Bundles the starting prompt, essential context, and trees
# into the clipboard.
#
# Two modes:
#   backs2   - full bundle for the backs2 trading engine (default)
#   portable - bridge docs + work-location tree only; no project-specific docs
#
# Usage:
#   .\docs\pack.ps1                                    interactive
#   .\docs\pack.ps1 -Mode backs2                       skip mode prompt
#   .\docs\pack.ps1 -Mode portable -Work C:\path       portable, no prompts
#   .\docs\pack.ps1 -Root C:\other -Mode backs2        scan a different repo

param(
    [string]$Root,
    [string]$Mode,
    [string]$Work,
    [switch]$NoPrompt
)

$ErrorActionPreference = 'Stop'

$packerRoot = Split-Path -Parent $PSScriptRoot

# ---- 0. Interactive prompts -------------------------------------------------

if (-not $Mode -and -not $NoPrompt) {
    Write-Host ''
    Write-Host "Mode: 'backs2' (default) or 'portable' [backs2]: " -NoNewline -ForegroundColor Cyan
    $a = Read-Host
    if ([string]::IsNullOrWhiteSpace($a)) { $Mode = 'backs2' } else { $Mode = $a.Trim().ToLower() }
}
if (-not $Mode) { $Mode = 'backs2' }
switch ($Mode) {
    'b' { $Mode = 'backs2' }
    'p' { $Mode = 'portable' }
}
if ($Mode -ne 'backs2' -and $Mode -ne 'portable') {
    Write-Host "Unknown mode '$Mode'; falling back to 'backs2'." -ForegroundColor Yellow
    $Mode = 'backs2'
}

if ($Mode -eq 'backs2') {
    if (-not $Root -and -not $NoPrompt) {
        Write-Host ''
        Write-Host ("Project root to scan [{0}]: " -f $packerRoot) -NoNewline -ForegroundColor Cyan
        $a = Read-Host
        if ([string]::IsNullOrWhiteSpace($a)) { $Root = $packerRoot } else { $Root = $a.Trim() }
    }
    if (-not $Root) { $Root = $packerRoot }
    if (-not [System.IO.Path]::IsPathRooted($Root)) { $Root = Join-Path (Get-Location) $Root }
    $Root = (Resolve-Path -Path $Root -ErrorAction Stop).Path
} else {
    # portable: root == work location
    if (-not $Work -and -not $NoPrompt) {
        Write-Host ''
        Write-Host 'Work location (folder the model should treat as its working directory): ' -NoNewline -ForegroundColor Cyan
        $a = Read-Host
        if (-not [string]::IsNullOrWhiteSpace($a)) { $Work = $a.Trim() }
    }
    if (-not $Work) {
        Write-Host 'Portable mode requires a work location. Aborting.' -ForegroundColor Red
        exit 1
    }
    if (-not [System.IO.Path]::IsPathRooted($Work)) { $Work = Join-Path (Get-Location) $Work }
    $Root = (Resolve-Path -Path $Work -ErrorAction Stop).Path
}

Push-Location $Root
try {
    Write-Host ''
    Write-Host ("Packing from: {0}  [mode: {1}]" -f $Root, $Mode) -ForegroundColor DarkGray

    $sb = [System.Text.StringBuilder]::new()

    $textExtensions = @(
        '.txt', '.md', '.cs', '.axaml', '.csproj', '.sln', '.json', '.yml', '.yaml',
        '.ps1', '.sh', '.bat', '.cmd', '.xml', '.config', '.toml', '.ini', '.go'
    )
    $excludeNames = @('pack.ps1', 'starting_promt.txt')

    function Find-BridgeDoc {
        param([string]$Name)
        $candidates = @(
            (Join-Path $Root ('tools\exec-bridge\' + $Name)),
            (Join-Path $packerRoot ('tools\exec-bridge\' + $Name))
        )
        foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
        return $null
    }

    # -- bridge docs (both modes) ------------------------------------------
    $externalIncludePaths = @(
        (Find-BridgeDoc 'README.md'),
        (Find-BridgeDoc 'PROTOCOL.txt'),
        (Find-BridgeDoc 'TOOLCALLS.txt')
    ) | Where-Object { $_ -ne $null }

    # -- starting prompt: backs2's (always) or work-location's if present --
    if ($Mode -eq 'portable') {
        $candidate = Join-Path $Root 'docs\starting_promt.txt'
        $startingPromptPath = if (Test-Path $candidate) { $candidate }
                              else { Join-Path $packerRoot 'docs\starting_promt.txt' }
    } else {
        $candidate = Join-Path $Root 'docs\starting_promt.txt'
        $startingPromptPath = if (Test-Path $candidate) { $candidate }
                              else { Join-Path $packerRoot 'docs\starting_promt.txt' }
    }
    if (-not (Test-Path $startingPromptPath)) {
        Write-Host 'starting_promt.txt not found; aborting.' -ForegroundColor Red
        exit 1
    }

    $docs = @()
    $omittedDocs = @()

    if ($Mode -eq 'backs2') {
        $coreNames = @(
            'CONTEXT_ESSENTIALS.md','AGENT_RULES.md','ROADMAP.md',
            'LONGWORK_FORMAT.md','PROTOCOL.txt','watch_downloads.go'
        )
        $allDocs = @(
            Get-ChildItem -Path 'docs' -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.FullName -notmatch '\\(bin|obj)\\' -and
                    $_.Name -notin $excludeNames -and
                    ($textExtensions -contains $_.Extension.ToLower())
                }
        )
        foreach ($p in $externalIncludePaths) { $allDocs += Get-Item $p }
        $allDocs = @($allDocs | Sort-Object FullName -Unique)
        $docs        = @($allDocs | Where-Object { $_.Name -in $coreNames })
        $omittedDocs = @($allDocs | Where-Object { $_.Name -notin $coreNames })
    } else {
        # portable: only bridge docs
        foreach ($p in $externalIncludePaths) { $docs += Get-Item $p }
        $docs = @($docs | Sort-Object FullName -Unique)
    }

    $modeLabel = if ($Mode -eq 'portable') { 'Portable' } else { 'Essentials' }

    # --- 0. Bundle header ---
    $generated = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $repoName  = Split-Path $Root -Leaf
    [void]$sb.AppendLine('########################## BUNDLE HEADER ##########################')
    [void]$sb.AppendLine("Generated:  $generated")
    [void]$sb.AppendLine("Repo:       $repoName")
    [void]$sb.AppendLine("Root:       $Root")
    [void]$sb.AppendLine("Mode:       $modeLabel")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('How to use this bundle:')
    if ($Mode -eq 'backs2') {
        [void]$sb.AppendLine('  1. Read the ENTIRE bundle before answering.')
        [void]$sb.AppendLine('  2. docs\starting_promt.txt is the highest-priority instruction set.')
        [void]$sb.AppendLine('  3. docs\CONTEXT_ESSENTIALS.md is the distilled project reference.')
        [void]$sb.AppendLine('  4. docs\AGENT_RULES.md governs every response (Output Contract).')
        [void]$sb.AppendLine('  5. Docs not bundled are listed at the end with request-on-demand commands.')
        [void]$sb.AppendLine('  6. If any doc conflicts with the starting prompt, the prompt wins.')
    } else {
        [void]$sb.AppendLine('  1. This is a PORTABLE bundle. There is no project context.')
        [void]$sb.AppendLine('  2. The work location below is your working directory.')
        [void]$sb.AppendLine('  3. Only the bridge protocol docs are supplied.')
        [void]$sb.AppendLine('  4. Do not assume anything about a specific codebase.')
        [void]$sb.AppendLine('  5. Do not request project docs unless the user names them.')
    }
    [void]$sb.AppendLine('  6. If the request needs context, summarize what you read in <=5 bullets.')
    [void]$sb.AppendLine('  7. Do not analyze this bundle unless the request requires it.')
    [void]$sb.AppendLine('###################################################################')
    [void]$sb.AppendLine()

    if ($Mode -eq 'portable') {
        [void]$sb.AppendLine('================= WORK LOCATION =================')
        [void]$sb.AppendLine($Root)
        [void]$sb.AppendLine()
    }

    # --- 0b. Table of contents ---
    [void]$sb.AppendLine('================= TABLE OF CONTENTS =================')
    $i = 1
    [void]$sb.AppendLine(('  [{0}] {1}' -f $i, $startingPromptPath)); $i++
    foreach ($doc in $docs) {
        $rel = $doc.FullName.Substring($Root.Length).TrimStart('\')
        if (-not $rel) { $rel = $doc.FullName }
        [void]$sb.AppendLine(('  [{0}] {1}' -f $i, $rel)); $i++
    }
    if ($Mode -ne 'portable') {
        [void]$sb.AppendLine(('  [{0}] MARKDOWN DOCS TREE' -f $i)); $i++
        [void]$sb.AppendLine(('  [{0}] CODE TREE' -f $i)); $i++
        [void]$sb.AppendLine(('  [{0}] DOCS NOT BUNDLED (request on demand)' -f $i)); $i++
    }
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
    Add-Section 'starting_promt.txt' (Get-Content $startingPromptPath -Raw)

    # --- 2. Selected docs ---
    $secretHits  = [System.Collections.Generic.List[string]]::new()
    $secretRegex = '(?i)\b(password|passwd|secret|api[_-]?key|access[_-]?token|bearer)\b\s*[:=]'

    foreach ($doc in $docs) {
        $rel = $doc.FullName.Substring($Root.Length).TrimStart('\')
        if (-not $rel) { $rel = $doc.FullName }
        $body = Get-Content $doc.FullName -Raw
        if ($body -match $secretRegex) { $secretHits.Add($rel) }
        Add-Section $rel $body
    }

    # --- 3. Trees ---
    $skipPattern = '\\(bin|obj|\.git|\.vs|\.vscode|node_modules|logs|\.metreja)\\'
    $dataSkip    = '\\data\\(cache|shots|parsed|ticks|optimizations)\\'
    $bridgeData  = '\\tools\\exec-bridge\\data\\'

    function Emit-Tree {
        param([string]$Header, [scriptblock]$Filter)
        [void]$sb.AppendLine($Header)
        Get-ChildItem -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { & $Filter $_ } |
            ForEach-Object { $_.FullName.Substring($Root.Length).TrimStart('\') } |
            Sort-Object |
            ForEach-Object { [void]$sb.AppendLine($_) }
        [void]$sb.AppendLine()
    }

    if ($Mode -eq 'backs2') {
        Emit-Tree '================= MARKDOWN DOCS TREE =================' {
            param($f)
            $f.Extension -eq '.md' -and
            $f.FullName -notmatch $skipPattern -and
            $f.FullName -notmatch $dataSkip -and
            $f.FullName -notmatch $bridgeData
        }
        $codeExt = @(
            '.cs','.axaml','.csproj','.sln','.ps1','.psm1',
            '.go','.js','.ts','.tsx','.jsx','.html','.css',
            '.mql5','.py','.sh','.bat','.cmd',
            '.yml','.yaml','.xml','.config','.toml','.ini',
            '.json','.editorconfig'
        )
        Emit-Tree '================= CODE TREE =================' {
            param($f)
            $f.Name -notlike '*.g.cs' -and
            $f.FullName -notmatch $skipPattern -and
            $f.FullName -notmatch $dataSkip -and
            $f.FullName -notmatch $bridgeData -and
            (
                $codeExt -contains $f.Extension.ToLower() -or
                $f.Name -like '.env*' -or
                $f.Name -eq '.gitignore' -or
                $f.Name -eq '.gitattributes'
            )
        }
    } else {
        Emit-Tree '================= WORK LOCATION TREE =================' {
            param($f)
            $f.FullName -notmatch $skipPattern -and
            $f.FullName -notmatch $dataSkip
        }
    }

    # --- 4. Docs not bundled ---
    [void]$sb.AppendLine('================= DOCS NOT BUNDLED (request on demand) =================')
    if ($Mode -eq 'portable' -or $omittedDocs.Count -eq 0) {
        [void]$sb.AppendLine('Nothing to request; every applicable doc is inlined above.')
    }
    else {
        [void]$sb.AppendLine('Request one at a time when the task touches its subject.')
        [void]$sb.AppendLine()
        foreach ($doc in $omittedDocs) {
            $rel = $doc.FullName.Substring($Root.Length).TrimStart('\')
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
    if ($Mode -eq 'backs2') {
        [void]$sb.AppendLine('  - You have read docs\starting_promt.txt and can list its rules.')
        [void]$sb.AppendLine('  - You have read docs\CONTEXT_ESSENTIALS.md - the distilled architecture.')
        [void]$sb.AppendLine('  - You will follow the Output Contract in docs\AGENT_RULES.md.')
    } else {
        [void]$sb.AppendLine('  - This is a PORTABLE bundle; no project context is available.')
        [void]$sb.AppendLine('  - The work location above is your working directory.')
    }
    [void]$sb.AppendLine('  - Every command you emit is inside a ```powershell fenced block.')
    [void]$sb.AppendLine('  - You will follow the starting prompt over any conflicting doc.')
    [void]$sb.AppendLine('  - You will scope the answer to the requested task only.')
    [void]$sb.AppendLine('==================================================================')

    # --- 6. Copy + stats ---
    $text          = $sb.ToString()
    Set-Clipboard -Value $text

    $lineCount     = ($text -split "`n").Count
    $charCount     = $text.Length
    $tokenEstimate = [math]::Round($charCount / 4.0)

    Write-Host ("Packed {0} lines / {1} chars (~{2} tokens). Mode: {3}." -f `
        $lineCount, $charCount, $tokenEstimate, $modeLabel) -ForegroundColor Green

    if ($tokenEstimate -gt 40000) {
        Write-Host 'WARNING: bundle is large (>40k tokens).' -ForegroundColor Yellow
    }
    if ($secretHits.Count -gt 0) {
        Write-Host 'WARNING: possible secrets detected in:' -ForegroundColor Yellow
        foreach ($h in $secretHits) { Write-Host "  - $h" -ForegroundColor Yellow }
    }

    Write-Host 'Paste into the chat/agent as needed.' -ForegroundColor DarkGray
}
finally {
    Pop-Location
}

# --- 7. Rebuild and launch the exec-bridge daemon + Q&A terminal ---
Write-Host
Write-Host 'Press ENTER to rebuild and launch the exec-bridge daemon + Q&A terminal, or type anything and press ENTER to exit:' -ForegroundColor Cyan
$response = Read-Host
if ([string]::IsNullOrWhiteSpace($response)) {
    $bridgeDir = Join-Path $Root 'tools\exec-bridge'
    if (-not (Test-Path $bridgeDir)) {
        $fallback = Join-Path $packerRoot 'tools\exec-bridge'
        if (Test-Path $fallback) {
            Write-Host ("Bridge not in target; using packer's own: {0}" -f $fallback) -ForegroundColor DarkGray
            $bridgeDir = $fallback
        }
    }
    $exePath = Join-Path $bridgeDir 'exec_bridge.exe'

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

            $daemonCmd = "Set-Location -LiteralPath '$Root'; & '$exePath'"
            Start-Process -FilePath $shellExe -ArgumentList '-NoExit', '-NoProfile', '-Command', $daemonCmd | Out-Null
            Write-Host ('  daemon (logging) launched, cwd={0}' -f $Root) -ForegroundColor Green

            Start-Sleep -Milliseconds 800

            Start-Process -FilePath $shellExe -ArgumentList '-NoExit', '-NoProfile', '-Command', "& '$exePath' -qterm" | Out-Null
            Write-Host '  Q&A terminal launched' -ForegroundColor Green
        } else {
            Write-Host 'Build failed; daemon not launched.' -ForegroundColor Red
        }
    }
}