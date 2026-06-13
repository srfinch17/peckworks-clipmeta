<#
.SYNOPSIS
    Builds the clipmetamcp MCP server and packs it into a Claude Desktop bundle (clipmeta.mcpb).

.DESCRIPTION
    Publishes clipmetamcp as a self-contained single-file win-x64 executable (no .NET install
    needed on the target machine), stages it with the bundle manifest, and zips the result.
    A .mcpb is a plain zip archive — no Node, no npm, no mcpb CLI required (spec §4).

    Output: dist/clipmeta.mcpb at the repo root. Install in Claude Desktop via
    Settings -> Extensions -> Advanced settings -> Extension Developer -> Install Extension…
    (there is NO drag-and-drop target — see PITFALLS 2026-06-12).

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

# Deletes a directory tree, retrying briefly on transient sharing violations. On Windows the
# antivirus/Search-indexer often grabs a just-written file for a second or two, so a Remove-Item
# immediately after staging can fail with "being used by another process" — observed on this
# repo. Retry with a short backoff rather than failing a clean pack over a transient lock.
function Remove-DirWithRetry([string]$path) {
    if (-not (Test-Path $path)) { return }
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try { Remove-Item $path -Recurse -Force -ErrorAction Stop; return }
        catch [System.IO.IOException] {
            if ($attempt -eq 5) { throw }
            Start-Sleep -Milliseconds (200 * $attempt)
        }
        catch [System.UnauthorizedAccessException] {
            if ($attempt -eq 5) { throw }
            Start-Sleep -Milliseconds (200 * $attempt)
        }
    }
}

# ── 1. Publish: one self-contained exe, runtime bundled ─────────────────────────────
Write-Host "Publishing clipmetamcp ($Configuration / $Runtime)..."
dotnet publish $projectDir -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exePath = Join-Path $publishDir 'clipmetamcp.exe'
if (-not (Test-Path $exePath)) { throw "expected publish output not found: $exePath" }

# ── 1b. Version gate: manifest and exe must agree ────────────────────────────────────
# The server advertises its assembly InformationalVersion (set in clipmetamcp.csproj) in the
# MCP initialize result; the manifest version is what Claude Desktop displays for the bundle.
# Shipping a bundle where they disagree means the UI and the protocol report different
# versions — fail the pack instead. (ProductVersion may carry a '+<commit>' suffix; the
# user-facing version is the part before it, matching McpSession.ReadAssemblyVersion.)
$manifest   = Get-Content (Join-Path $PSScriptRoot 'mcpb-manifest.json') -Raw | ConvertFrom-Json
$exeVersion = ((Get-Item $exePath).VersionInfo.ProductVersion -split '\+')[0]
if (-not $exeVersion) { throw "could not read ProductVersion from $exePath" }
if ($exeVersion -ne $manifest.version) {
    throw ("version mismatch: tools/mcpb-manifest.json says '{0}' but clipmetamcp.exe says '{1}'. " +
           "Update <InformationalVersion> in clipmetamcp/clipmetamcp.csproj and the manifest together." `
           -f $manifest.version, $exeVersion)
}

# ── 2. Stage the bundle layout: manifest.json + server/clipmetamcp.exe ──────────────
Remove-DirWithRetry $stageDir
New-Item -ItemType Directory -Force (Join-Path $stageDir 'server') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'mcpb-manifest.json') (Join-Path $stageDir 'manifest.json')
Copy-Item $exePath (Join-Path $stageDir 'server\clipmetamcp.exe')

# ── 2b. Keep the unpacked layout as a first-class artifact ──────────────────────────
# The Microsoft Store build of Claude Desktop fails to install packed .mcpb files (silent
# no-op; see PITFALLS 2026-06-12). The working install path there is "Install Unpacked
# Extension" pointed at exactly this folder, so it ships next to the bundle.
$unpackedDir = Join-Path $distDir 'clipmeta-unpacked'
Remove-DirWithRetry $unpackedDir
Copy-Item $stageDir $unpackedDir -Recurse

# ── 3. Zip: a .mcpb is just a zip with a manifest at its root ───────────────────────
# System.IO.Compression.ZipFile (not Compress-Archive) for one reason: it always writes
# forward-slash entry names per the ZIP spec. Compress-Archive under Windows PowerShell 5.1
# emits backslash entries ('server\clipmetamcp.exe'), which spec-strict extractors reject —
# producing an installed-but-never-spawns bundle. ZipFile makes the output identical no matter
# which PowerShell runs this script.
Remove-Item $mcpbPath -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $mcpbPath)
Remove-DirWithRetry $stageDir

$sizeMb = (Get-Item $mcpbPath).Length / 1MB
Write-Host ("Packed {0}  ({1:N1} MB)" -f $mcpbPath, $sizeMb)
Write-Host 'Install: Claude Desktop -> Settings -> Extensions -> Advanced settings -> Install Extension... -> pick this file, then choose your clips folder.'
Write-Host 'Microsoft Store build of Claude Desktop? Packed install silently fails (PITFALLS 2026-06-12) - use Install Unpacked Extension on dist/clipmeta-unpacked instead.'
