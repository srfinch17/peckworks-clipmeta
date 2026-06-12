<#
.SYNOPSIS
    Builds the clipmetamcp MCP server and packs it into a Claude Desktop bundle (clipmeta.mcpb).

.DESCRIPTION
    Publishes clipmetamcp as a self-contained single-file win-x64 executable (no .NET install
    needed on the target machine), stages it with the bundle manifest, and zips the result.
    A .mcpb is a plain zip archive — no Node, no npm, no mcpb CLI required (spec §4).

    Output: dist/clipmeta.mcpb at the repo root. Install by dragging onto
    Claude Desktop → Settings → Extensions.

.NOTES
    PublishTrimmed must remain OFF (spec risk R4) — it is pinned in clipmetamcp.csproj;
    do not add -p:PublishTrimmed=true here.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path $PSScriptRoot -Parent
$projectDir = Join-Path $repoRoot 'clipmetamcp'
$publishDir = Join-Path $projectDir "bin\$Configuration\net10.0\$Runtime\publish"
$distDir    = Join-Path $repoRoot 'dist'
$stageDir   = Join-Path $distDir 'mcpb-stage'
$mcpbPath   = Join-Path $distDir 'clipmeta.mcpb'

# ── 1. Publish: one self-contained exe, runtime bundled ─────────────────────────────
Write-Host "Publishing clipmetamcp ($Configuration / $Runtime)..."
dotnet publish $projectDir -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exePath = Join-Path $publishDir 'clipmetamcp.exe'
if (-not (Test-Path $exePath)) { throw "expected publish output not found: $exePath" }

# ── 2. Stage the bundle layout: manifest.json + server/clipmetamcp.exe ──────────────
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stageDir 'server') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'mcpb-manifest.json') (Join-Path $stageDir 'manifest.json')
Copy-Item $exePath (Join-Path $stageDir 'server\clipmetamcp.exe')

# ── 3. Zip: a .mcpb is just a zip with a manifest at its root ───────────────────────
# System.IO.Compression.ZipFile (not Compress-Archive) for one reason: it always writes
# forward-slash entry names per the ZIP spec. Compress-Archive under Windows PowerShell 5.1
# emits backslash entries ('server\clipmetamcp.exe'), which spec-strict extractors reject —
# producing an installed-but-never-spawns bundle. ZipFile makes the output identical no matter
# which PowerShell runs this script.
Remove-Item $mcpbPath -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $mcpbPath)
Remove-Item $stageDir -Recurse -Force

$sizeMb = (Get-Item $mcpbPath).Length / 1MB
Write-Host ("Packed {0}  ({1:N1} MB)" -f $mcpbPath, $sizeMb)
Write-Host 'Install: drag onto Claude Desktop -> Settings -> Extensions, then pick your clips folder.'
