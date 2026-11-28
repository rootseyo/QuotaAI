<#
.SYNOPSIS
  Builds, signs, and publishes a QuotaAI for Windows preview release from this machine.

.DESCRIPTION
  1. Checks that `main` is clean, pushed, and the tag is new.
  2. Runs the Windows tests and publishes a self-contained single-file x64 build
     stamped with -Version.
  3. Signs QuotaAI.exe with the local release key (ECDSA P-256, SHA-256) and
     verifies the signature against Windows/update-signing.pub.pem, the key the
     in-app updater trusts.
  4. Pushes tag windows-preview-<Version> and creates a GitHub prerelease with
     QuotaAI.exe and QuotaAI.exe.sig.

  The private key never leaves this machine. Use -DryRun to stop after step 3.

.EXAMPLE
  ./scripts/release-windows.ps1 -Version 2026.11.29 -DryRun
  ./scripts/release-windows.ps1 -Version 2026.11.29
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$KeyPath = (Join-Path $HOME '.quotaai\windows-update-signing.pem'),

    [switch]$DryRun,

    # With -DryRun only: skip the clean/pushed checks to try out local changes.
    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Checked {
    param([Parameter(Mandatory)][scriptblock]$Command, [string]$What = 'command')
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)." }
}

function Find-Tool {
    param([string]$Name, [string[]]$Fallbacks = @())
    $found = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.Source }
    foreach ($candidate in $Fallbacks) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw "$Name was not found. Install it and try again."
}

$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

# --- Inputs -----------------------------------------------------------------
$parsed = $null
if (-not [version]::TryParse($Version, [ref]$parsed)) {
    throw "Version '$Version' is not a 2-4 part numeric version such as 2026.11.29 or 2026.11.29.1."
}
$tag = "windows-preview-$Version"
$publicKey = Join-Path $repo 'Windows\update-signing.pub.pem'
if (-not (Test-Path $KeyPath)) { throw "Signing key not found at $KeyPath." }
if (-not (Test-Path $publicKey)) { throw "Public key not found at $publicKey." }

$dotnet = Find-Tool 'dotnet'
$openssl = Find-Tool 'openssl' @(
    'C:\Program Files\Git\mingw64\bin\openssl.exe',
    'C:\Program Files\Git\usr\bin\openssl.exe')
$gh = $null
if (-not $DryRun) {
    $gh = Find-Tool 'gh' @('C:\Program Files\GitHub CLI\gh.exe')
    Invoke-Checked { & $gh auth status *> $null } 'gh auth status (run: gh auth login)'
}

# --- Git state --------------------------------------------------------------
if ($AllowDirty -and -not $DryRun) { throw '-AllowDirty can only be used with -DryRun.' }
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
Invoke-Checked { git fetch --quiet origin main --tags } 'git fetch'
$head = (git rev-parse HEAD).Trim()
if (-not $AllowDirty) {
    if ($branch -ne 'main') { throw "Release from main (currently on $branch)." }
    if (git status --porcelain) { throw 'The working tree has uncommitted changes. Commit or stash them first.' }
    if ($head -ne (git rev-parse origin/main).Trim()) { throw 'Local main differs from origin/main. Push or pull first.' }
}
if (git tag --list $tag) { throw "Tag $tag already exists." }

# The updater only installs versions newer than the running one.
$newestTag = git tag --list 'windows-preview-*' |
    ForEach-Object {
        $v = $null
        if ([version]::TryParse($_.Substring('windows-preview-'.Length), [ref]$v)) { $v }
    } | Sort-Object -Descending | Select-Object -First 1
if ($newestTag -and $parsed -le $newestTag) {
    throw "Version $Version must be newer than the latest Windows release ($newestTag)."
}

Write-Host "Releasing $tag from $($head.Substring(0, 7))" -ForegroundColor Cyan

# --- Build ------------------------------------------------------------------
Invoke-Checked { & $dotnet run --project Windows/QuotaAI.Tests -c Release } 'Windows tests'

$out = Join-Path $repo "Windows\artifacts\release-$Version"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Invoke-Checked {
    & $dotnet publish Windows/QuotaAI.Tray -c Release -r win-x64 "-p:Version=$Version" `
        --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
} 'dotnet publish'

$exe = Join-Path $out 'QuotaAI.exe'
$sig = "$exe.sig"
$fileVersion = [version](Get-Item $exe).VersionInfo.FileVersion
$expected = [version]::new($parsed.Major, $parsed.Minor, [Math]::Max($parsed.Build, 0), [Math]::Max($parsed.Revision, 0))
if ($fileVersion -ne $expected) { throw "Built exe reports version $fileVersion, expected $expected." }

# --- Sign -------------------------------------------------------------------
Invoke-Checked { & $openssl dgst -sha256 -sign $KeyPath -out $sig $exe } 'Signing'
Invoke-Checked { & $openssl dgst -sha256 -verify $publicKey -signature $sig $exe } 'Signature check against Windows/update-signing.pub.pem'

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Built and signed: $exe" -ForegroundColor Green
Write-Host "  size   $((Get-Item $exe).Length) bytes"
Write-Host "  sha256 $hash"

if ($DryRun) {
    Write-Host 'Dry run: nothing was tagged, pushed, or published.' -ForegroundColor Yellow
    return
}

# --- Publish ----------------------------------------------------------------
Invoke-Checked { git tag $tag $head } 'git tag'
Invoke-Checked { git push origin $tag } 'git push tag'
Invoke-Checked {
    & $gh release create $tag "$exe#QuotaAI for Windows (x64)" $sig `
        --verify-tag --prerelease --latest=false `
        --title "QuotaAI Windows Preview ($Version)" `
        --notes 'Standalone Windows x64 build. Download QuotaAI.exe to run the preview. QuotaAI.exe.sig is the signature checked by the in-app updater.'
} 'gh release create'

Write-Host "Published https://github.com/rootseyo/QuotaAI/releases/tag/$tag" -ForegroundColor Green
