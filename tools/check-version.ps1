<#
.SYNOPSIS
    Version drift check: does every ClipMeta artifact report the canonical VERSION?

.DESCRIPTION
    Reads the repo-root VERSION file, then asks each artifact its OWN version (a real self-report,
    not a re-read of source) and prints a per-artifact OK / DRIFT line:

      - clipmetascribe / clipmetaview / clipmetamcp : each binary's `--version` output
      - bundle manifest                             : tools/mcpb-manifest.json "version"

    Exits non-zero if anything drifts. By default it builds first so the binaries report their
    freshly-stamped version; pass -NoBuild to probe the existing build output instead.

    HONEST LIMITATION: this sees the REPO-BUILT exe, not whatever .mcpb is actually installed in
    Claude Desktop. A bump is not live in Desktop until you repack (tools/pack-mcpb.ps1) and
    reinstall — verify that by checking the version Desktop shows after reinstalling.
#>
[CmdletBinding()]
param([switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$expected = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()

Write-Host "Canonical VERSION: $expected"
Write-Host ""

if (-not $NoBuild) {
    Write-Host "Building (pass -NoBuild to skip)..."
    & dotnet build (Join-Path $repoRoot 'peckworks-clipmeta.slnx') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
    Write-Host ""
}

$buildArgs = if ($NoBuild) { @('--no-build') } else { @() }
$anyDrift = $false

function Show([string]$label, [string]$reported) {
    if ($reported -eq $script:expected) {
        Write-Host ("  OK     {0,-22} {1}" -f $label, $reported)
    } else {
        Write-Host ("  DRIFT  {0,-22} {1}  (expected {2})" -f $label, $reported, $script:expected)
        $script:anyDrift = $true
    }
}

function Get-CliVersion([string]$project) {
    $proj = Join-Path $repoRoot $project
    $runArgs = @('run', '--project', $proj) + $buildArgs + @('--', '--version')
    $out  = & dotnet @runArgs 2>$null
    $line = $out | Where-Object { $_ -match '\d+\.\d+\.\d+' } | Select-Object -Last 1
    if ($line) { return ($line.Trim() -split '\s+')[-1] } else { return '(no --version output)' }
}

Show 'clipmetascribe (--version)' (Get-CliVersion 'clipmetascribe')
Show 'clipmetaview (--version)'   (Get-CliVersion 'clipmetaview')
Show 'clipmetamcp (--version)'    (Get-CliVersion 'clipmetamcp')

$manifestVersion = (Get-Content (Join-Path $PSScriptRoot 'mcpb-manifest.json') -Raw | ConvertFrom-Json).version
Show 'bundle manifest' $manifestVersion

Write-Host ""
if ($anyDrift) {
    Write-Host "DRIFT detected. If you just bumped, rebuild/repack the drifted artifact; a bump is not"
    Write-Host "live in a built/installed artifact until it is rebuilt, repacked, and reinstalled."
    exit 1
}
Write-Host "All artifacts agree with VERSION ($expected)."
Write-Host "(Reminder: the .mcpb installed in Desktop is verified by reinstall, not by this check.)"
exit 0
