param (
    [string[]]$Files
)

Write-Output "================= PROJECT STRUCTURE ================="
# Get clean relative paths, including .env files, but ignoring build artifacts
Get-ChildItem -Recurse -File | 
    Where-Object { 
        $_.FullName -notmatch '\\(bin|obj|\.git|\.vs|\.vscode)\\' -and 
        $_.Name -notlike '*.g.cs' -and
        ($_.Extension -in '.cs', '.axaml', '.csproj' -or $_.Name -like '.env*')
    } | 
    Resolve-Path -Relative

# Print selected files content (with safety check for .env)
foreach ($file in $Files) {
    if (Test-Path $file) {
        $fileName = Split-Path $file -Leaf
        
        # Security Guardrail: Never print contents of sensitive .env files
        if ($fileName -like '.env*') {
            Write-Output "`n================= File: $file ================="
            Write-Output "[SECURITY NOTE: Content hidden to prevent leaking secrets/keys]"
        } else {
            Write-Output "`n================= File: $file ================="
            Get-Content $file -Raw
        }
    } else {
        Write-Output "`n[WARNING] File not found: $file"
    }
}