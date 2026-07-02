<#
.SYNOPSIS
    Version drift check: does every ClipMeta artifact report the canonical VERSION?

.DESCRIPTION
    Reads the repo-root VERSION file, then asks each artifact its OWN version (a real self-report,
    not a re-read of source) and prints a per-artifact OK / DRIFT line:

      - clipmetascribe / clipmetaview / clipmetamcp : each binary's `--version` output
      - bundle manifest                             : tools/mcpb-manifest.json "version"
      - landing page (download button)               : docs/index.html "Download vX.Y.Z (Windows)"
      - landing page (footer marker)                 : docs/index.html "clipmeta info page . vX.Y ."

    The landing page checks are a source re-read, not a self-report (a static page has none to give),
    but they use the same tightly anchored patterns tools/bump-version.ps1 writes, so a hand-edit that
    drifts from VERSION, or a page restructure that silently strands the old text, both surface here.

    Exits non-zero if anything drifts. By default it builds first so the binaries report their
    freshly-stamped version; pass -NoBuild to probe the existing build output instead.

    HONEST LIMITATION: this sees the REPO-BUILT exe, not whatever .mcpb is actually installed in
    Claude Desktop. A bump is not live in Desktop until you repack (tools/pack-mcpb.ps1) and
    reinstall, verify that by checking the version Desktop shows after reinstalling.
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

function Show([string]$label, [string]$reported, [string]$expectedValue = $script:expected) {
    if ($reported -eq $expectedValue) {
        Write-Host ("  OK     {0,-22} {1}" -f $label, $reported)
    } else {
        Write-Host ("  DRIFT  {0,-22} {1}  (expected {2})" -f $label, $reported, $expectedValue)
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

# Same anchors tools/bump-version.ps1 writes, kept in sync manually since a page re-stamp is a
# separate script from this read-only check.
$indexHtmlText = Get-Content (Join-Path $repoRoot 'docs/index.html') -Raw

function Get-IndexHtmlDownloadVersion([string]$text) {
    $m = [regex]::Match($text, 'Download v(\d+\.\d+\.\d+) \(Windows\)')
    if ($m.Success) { return $m.Groups[1].Value } else { return '(anchor not found)' }
}

function Get-IndexHtmlMetaLineVersion([string]$text) {
    $m = [regex]::Match($text, 'clipmeta info page · v(\d+\.\d+) ·')
    if ($m.Success) { return $m.Groups[1].Value } else { return '(anchor not found)' }
}

$expectedMajorMinor = ($expected -split '\.')[0, 1] -join '.'
Show 'landing page (download)' (Get-IndexHtmlDownloadVersion $indexHtmlText)
Show 'landing page (footer)'   (Get-IndexHtmlMetaLineVersion $indexHtmlText) $expectedMajorMinor

Write-Host ""
if ($anyDrift) {
    Write-Host "DRIFT detected. If you just bumped, rebuild/repack the drifted artifact; a bump is not"
    Write-Host "live in a built/installed artifact until it is rebuilt, repacked, and reinstalled."
    exit 1
}
Write-Host "All artifacts agree with VERSION ($expected)."
Write-Host "(Reminder: the .mcpb installed in Desktop is verified by reinstall, not by this check.)"
exit 0
