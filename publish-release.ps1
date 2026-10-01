#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes Subtitle Studio and creates a GitHub release for the current FullVersion.

.DESCRIPTION
    1. Reads <FullVersion> from the csproj by parsing it as XML.
    2. Checks CHANGELOG.md has a "## [<version>]" entry and uses it as the release notes.
    3. Publishes the single-file self-contained x64 EXE (unless -SkipBuild).
    4. Creates release tag v<version> with gh, attaching SubtitleStudio-v<version>-win-x64.exe.

    Every native command is checked through $LASTEXITCODE. Nothing is hidden with 2>$null.
    This file is ASCII only on purpose (Windows PowerShell 5.1 reads BOM-less files as ANSI).

.PARAMETER Draft
    Create the release as a draft.

.PARAMETER SkipBuild
    Use the EXE already in .\publish instead of publishing again.

.EXAMPLE
    .\publish-release.ps1
    .\publish-release.ps1 -Draft
#>
[CmdletBinding()]
param(
    [switch]$Draft,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root        = $PSScriptRoot
$ProjectPath = Join-Path $Root 'src\SubtitleStudio\SubtitleStudio.csproj'
$Changelog   = Join-Path $Root 'CHANGELOG.md'
$PublishDir  = Join-Path $Root 'publish'
$DistDir     = Join-Path $Root 'dist'

function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }

function Fail([string]$Text) {
    Write-Host ''
    Write-Host "ERROR: $Text" -ForegroundColor Red
    exit 1
}

# Runs a native program, returns @{ Code; Output }. Stderr is captured, not discarded, and
# EAP is relaxed only for the call so PS 5.1 does not turn stderr lines into terminating errors.
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [switch]$Echo
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = & $FilePath @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($Echo) { $lines | ForEach-Object { Write-Host $_ } }
    return [pscustomobject]@{ Code = $code; Output = ($lines -join [Environment]::NewLine) }
}

# ---------------------------------------------------------------- version
Write-Step 'Reading version from csproj'
if (-not (Test-Path -LiteralPath $ProjectPath)) { Fail "Project not found: $ProjectPath" }

[xml]$projectXml = Get-Content -LiteralPath $ProjectPath -Raw
$versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/FullVersion')
if ($null -eq $versionNode) { Fail 'No <FullVersion> element found in the csproj.' }

$Version = $versionNode.InnerText.Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { Fail "FullVersion '$Version' is not a four-part version." }
$Tag = "v$Version"
Write-Host "Version: $Version  (tag $Tag)"

# ---------------------------------------------------------------- tools
Write-Step 'Checking tools'
foreach ($tool in @('dotnet', 'gh', 'git')) {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "'$tool' was not found on PATH." }
}

$auth = Invoke-Native -FilePath 'gh' -Arguments @('auth', 'status')
if ($auth.Code -ne 0) { Fail "gh is not logged in. Run 'gh auth login' first.`n$($auth.Output)" }

$status = Invoke-Native -FilePath 'git' -Arguments @('status', '--porcelain')
if ($status.Code -ne 0) { Fail "git status failed:`n$($status.Output)" }
if ($status.Output.Trim().Length -gt 0) {
    Write-Host 'WARNING: the working tree has uncommitted changes. The release is created from the pushed commit, not your local files.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- changelog
Write-Step 'Extracting release notes from CHANGELOG.md'
if (-not (Test-Path -LiteralPath $Changelog)) { Fail 'CHANGELOG.md not found.' }

$changelogLines = Get-Content -LiteralPath $Changelog -Encoding UTF8
$headerPattern = '^## \[' + [regex]::Escape($Version) + '\]'
$start = -1
for ($i = 0; $i -lt $changelogLines.Count; $i++) {
    if ($changelogLines[$i] -match $headerPattern) { $start = $i; break }
}
if ($start -lt 0) { Fail "CHANGELOG.md has no '## [$Version]' entry. Add one before releasing." }

$notes = New-Object System.Collections.Generic.List[string]
for ($i = $start + 1; $i -lt $changelogLines.Count; $i++) {
    if ($changelogLines[$i] -match '^## ') { break }
    $notes.Add($changelogLines[$i])
}
$notesText = ($notes -join "`n").Trim()
if ($notesText.Length -eq 0) { Fail "The CHANGELOG entry for $Version is empty." }

# ---------------------------------------------------------------- tag must be new
Write-Step "Checking that release $Tag does not exist yet"
$existing = Invoke-Native -FilePath 'gh' -Arguments @('release', 'view', $Tag)
if ($existing.Code -eq 0) { Fail "Release $Tag already exists. Bump <FullVersion> in the csproj first." }

# ---------------------------------------------------------------- build
if (-not $SkipBuild) {
    Write-Step 'Publishing single-file EXE'
    if (Test-Path -LiteralPath $PublishDir) { Remove-Item -LiteralPath $PublishDir -Recurse -Force }
    $publishArgs = @('publish', $ProjectPath, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
                     '-p:PublishSingleFile=true', '-o', $PublishDir)
    $publish = Invoke-Native -FilePath 'dotnet' -Arguments $publishArgs -Echo
    if ($publish.Code -ne 0) { Fail "dotnet publish failed (exit code $($publish.Code))." }
}

$exe = Get-ChildItem -LiteralPath $PublishDir -Filter 'SubtitleStudio*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $exe) { Fail "No SubtitleStudio*.exe found in $PublishDir." }

$exeVersion = $exe.VersionInfo.FileVersion
if ($exeVersion -ne $Version) { Fail "EXE FileVersion is '$exeVersion' but the csproj says '$Version'. Rebuild without -SkipBuild." }

# ---------------------------------------------------------------- stage assets
Write-Step 'Staging release asset'
if (-not (Test-Path -LiteralPath $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }
$assetPath = Join-Path $DistDir "SubtitleStudio-$Tag.exe"
Copy-Item -LiteralPath $exe.FullName -Destination $assetPath -Force

$notesPath = Join-Path $DistDir "release-notes-$Tag.md"
[System.IO.File]::WriteAllText($notesPath, $notesText, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Asset: $assetPath"

# ---------------------------------------------------------------- release
Write-Step "Creating GitHub release $Tag"
$releaseArgs = @('release', 'create', $Tag, $assetPath, '--title', "Subtitle Studio $Tag", '--notes-file', $notesPath)
if ($Draft) { $releaseArgs += '--draft' }

$release = Invoke-Native -FilePath 'gh' -Arguments $releaseArgs -Echo
if ($release.Code -ne 0) { Fail "gh release create failed (exit code $($release.Code))." }

Write-Host ''
Write-Host "Done. Release $Tag created." -ForegroundColor Green
exit 0
