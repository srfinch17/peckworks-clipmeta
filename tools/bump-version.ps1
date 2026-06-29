<#
.SYNOPSIS
    Deliberately changes the canonical ClipMeta product version.

.DESCRIPTION
    Rewrites the repo-root VERSION file and re-stamps the bundle manifest (tools/mcpb-manifest.json)
    so the two never drift in git. Assemblies pick up the new version on the next build (via
    Directory.Build.props); the installed .mcpb picks it up only after tools/pack-mcpb.ps1 and a
    Desktop reinstall. Run tools/check-version.ps1 afterward to confirm.

    A bump is deliberate — run this when shipping, not on every commit. Bumping does not rebuild,
    repack, or reinstall anything; it only changes the source of truth.

.PARAMETER Part
    'major' | 'minor' | 'patch' to increment, or 'set' to use an explicit value.

.PARAMETER Value
    Required when Part is 'set': the explicit SemVer (e.g. 1.0.0).

.EXAMPLE
    ./tools/bump-version.ps1 set 1.0.0
    ./tools/bump-version.ps1 patch
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('major', 'minor', 'patch', 'set')][string]$Part,
    [string]$Value
)

$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path $PSScriptRoot -Parent
$versionPath  = Join-Path $repoRoot 'VERSION'
$manifestPath = Join-Path $PSScriptRoot 'mcpb-manifest.json'

$current = (Get-Content $versionPath -Raw).Trim()
if ($current -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION is not a plain SemVer 'major.minor.patch': '$current'"
}

if ($Part -eq 'set') {
    if (-not $Value) { throw "Part 'set' requires a Value, e.g. ./bump-version.ps1 set 1.0.0" }
    if ($Value -notmatch '^\d+\.\d+\.\d+$') { throw "Value is not a SemVer 'major.minor.patch': '$Value'" }
    $next = $Value
}
else {
    $parts = $current.Split('.')
    [int]$major = $parts[0]; [int]$minor = $parts[1]; [int]$patch = $parts[2]
    switch ($Part) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
    $next = "$major.$minor.$patch"
}

# Canonical source.
Set-Content -Path $versionPath -Value $next -NoNewline

# Re-stamp the manifest with a targeted text edit (preserves the hand-formatted layout). Matches
# the standalone "version" key only — "manifest_version" has a non-quote char before it.
$manifestText = (Get-Content $manifestPath -Raw) -replace '("version"\s*:\s*")[^"]*(")', "`${1}$next`${2}"
Set-Content -Path $manifestPath -Value $manifestText -NoNewline

Write-Host "VERSION: $current -> $next"
Write-Host "Stamped tools/mcpb-manifest.json -> $next"
Write-Host "Next: dotnet build; tools/pack-mcpb.ps1; reinstall the .mcpb in Desktop. Then tools/check-version.ps1."
