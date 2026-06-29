<#
.SYNOPSIS
    Builds the three downloadable release assets into dist/.

.DESCRIPTION
    Produces, into dist/:
      clipmeta.mcpb              the Claude Desktop bundle (via pack-mcpb.ps1)
      clipmeta-unpacked.zip      the same bundle unpacked (Microsoft Store fallback)
      clipmeta-cli-win-x64.zip   self-contained clipmetascribe.exe + clipmetaview.exe + README.txt

    Runnable locally and from .github/workflows/release.yml. Version is read from the repo-root
    VERSION file (the single canonical source; see CLAUDE.md "Versioning") and stamped into the
    CLI README.

.NOTES
    Windows + PowerShell 7. The CLIs are self-contained single-file win-x64 (no .NET needed by the
    user). PublishTrimmed stays OFF (reflection-based System.Text.Json, same reason as the MCP exe).
#>
[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$Runtime = 'win-x64')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dist     = Join-Path $repoRoot 'dist'
$version  = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host "== Building clipmeta release assets v$version ($Configuration / $Runtime) =="

# 1. clipmeta.mcpb + dist/clipmeta-unpacked/ (also stamps + gates the manifest against VERSION).
& (Join-Path $PSScriptRoot 'pack-mcpb.ps1') -Configuration $Configuration -Runtime $Runtime

# 2. Zip the unpacked bundle (the Microsoft Store fallback). ZipFile, not Compress-Archive, for
#    deterministic forward-slash entries (PITFALLS 2026-06-11).
$unpackedZip = Join-Path $dist 'clipmeta-unpacked.zip'
Remove-Item $unpackedZip -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $dist 'clipmeta-unpacked'), $unpackedZip)

# 3. Publish the CLIs self-contained single-file, stage with a versioned README, and zip.
$stage = Join-Path $dist 'cli-stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($proj in 'clipmetascribe', 'clipmetaview') {
    $out = Join-Path $dist "_$proj"
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish (Join-Path $repoRoot $proj) -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo -v q -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $proj failed (exit $LASTEXITCODE)" }
    Copy-Item (Join-Path $out "$proj.exe") $stage
}
$readme = (Get-Content (Join-Path $PSScriptRoot 'cli-readme.txt') -Raw) -replace '\{\{VERSION\}\}', $version
Set-Content -Path (Join-Path $stage 'README.txt') -Value $readme -NoNewline

$cliZip = Join-Path $dist 'clipmeta-cli-win-x64.zip'
Remove-Item $cliZip -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $cliZip)

# Tidy the intermediate publish/staging dirs so dist/ holds just the three assets.
Remove-Item $stage, (Join-Path $dist '_clipmetascribe'), (Join-Path $dist '_clipmetaview') `
    -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "== Done. Release assets in dist/ =="
Get-ChildItem (Join-Path $dist 'clipmeta.mcpb'), $unpackedZip, $cliZip |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
