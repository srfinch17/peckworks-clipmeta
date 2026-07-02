<#
.SYNOPSIS
    Deliberately changes the canonical ClipMeta product version.

.DESCRIPTION
    Rewrites the repo-root VERSION file, re-stamps the bundle manifest (tools/mcpb-manifest.json),
    and re-stamps the version literals on the public landing page (docs/index.html) so none of them
    ever drift in git. Assemblies pick up the new version on the next build (via
    Directory.Build.props); the installed .mcpb picks it up only after tools/pack-mcpb.ps1 and a
    Desktop reinstall. Run tools/check-version.ps1 afterward to confirm.

    The landing page is a curated, hand-built artifact (see CLAUDE.md), so its two version literals
    are re-stamped with tightly anchored text replacements, not a whole-file regex: the "Download
    vX.Y.Z (Windows)" button gets the full SemVer, the "clipmeta info page . vX.Y ." footer line gets
    major.minor only (it is a page-revision marker, not a full-precision build stamp). Each anchor
    must match exactly once, if a future page edit changes or removes an anchor, this script throws
    rather than silently leaving the page stranded on the old version.

    A bump is deliberate, run this when shipping, not on every commit. Bumping does not rebuild,
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
$indexHtmlPath = Join-Path $repoRoot 'docs/index.html'

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
# the standalone "version" key only, "manifest_version" has a non-quote char before it.
$manifestText = (Get-Content $manifestPath -Raw) -replace '("version"\s*:\s*")[^"]*(")', "`${1}$next`${2}"
Set-Content -Path $manifestPath -Value $manifestText -NoNewline

$nextParts     = $next.Split('.')
$nextMajorMinor = "$($nextParts[0]).$($nextParts[1])"

# Re-stamp the landing page with two tightly anchored text replacements (it is a hand-curated
# artifact, never touched by a whole-file regex). Each anchor must appear exactly once, a missing
# or duplicated anchor throws rather than silently stranding the page on the old version.
$indexHtmlText = Get-Content $indexHtmlPath -Raw

$downloadPattern = 'Download v\d+\.\d+\.\d+ \(Windows\)'
$downloadMatches = [regex]::Matches($indexHtmlText, $downloadPattern)
if ($downloadMatches.Count -ne 1) {
    throw "Expected exactly one 'Download vX.Y.Z (Windows)' anchor in docs/index.html, found $($downloadMatches.Count). The page may have been edited, update the anchor pattern in bump-version.ps1 before re-running."
}
$indexHtmlText = [regex]::Replace($indexHtmlText, $downloadPattern, "Download v$next (Windows)")

# The literal middle-dot separator (U+00B7) below must byte-match docs/index.html's own UTF-8
# encoding; this file is saved UTF-8 without BOM to keep it that way.
$metaLinePattern = 'clipmeta info page · v\d+\.\d+ ·'
$metaLineMatches = [regex]::Matches($indexHtmlText, $metaLinePattern)
if ($metaLineMatches.Count -ne 1) {
    throw "Expected exactly one 'clipmeta info page . vX.Y .' anchor in docs/index.html, found $($metaLineMatches.Count). The page may have been edited, update the anchor pattern in bump-version.ps1 before re-running."
}
$indexHtmlText = [regex]::Replace($indexHtmlText, $metaLinePattern, "clipmeta info page `u{00B7} v$nextMajorMinor `u{00B7}")

Set-Content -Path $indexHtmlPath -Value $indexHtmlText -NoNewline

Write-Host "VERSION: $current -> $next"
Write-Host "Stamped tools/mcpb-manifest.json -> $next"
Write-Host "Stamped docs/index.html -> download v$next, page marker v$nextMajorMinor"
Write-Host "Next: dotnet build; tools/pack-mcpb.ps1; reinstall the .mcpb in Desktop. Then tools/check-version.ps1."
