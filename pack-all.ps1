<#
.SYNOPSIS
  Builds every distributable for McpOrchestrator into ./dist.

.DESCRIPTION
  Three tiers of artifacts, so each consumer gets a fitting install option:

   1. Portable .NET tool package (framework-dependent — needs the .NET runtime installed; one
      package serves all platforms):
        McpOrchestrator.<version>.nupkg                        (~1.4 MB)
      Install:  dotnet tool install --global McpOrchestrator

   2. Self-contained tool packages (per-RID; installed the SAME way via `dotnet tool`, but bundle
      the runtime so they don't need a matching .NET runtime — the dotnet CLI is still needed to
      install). The runtime id is in the package id so platforms coexist on a feed:
        McpOrchestrator.<rid>.<version>.nupkg
      Install:  dotnet tool install --global McpOrchestrator.win-x64

   3. Self-contained zips (per-RID; the only option that needs NO .NET at all — unzip and run):
        McpOrchestrator-<version>-<rid>-selfcontained.zip      (single-file)

  RIDs cover win/linux/osx on x64 and arm64. Cross-RID builds work from any OS.

.PARAMETER Version            Package version (default 0.1.0).
.PARAMETER OutDir             Output directory (default ./dist).
.PARAMETER Rids               Runtime identifiers for the self-contained artifacts.
.PARAMETER SkipTools          Skip the portable framework-dependent tool package (tier 1).
.PARAMETER SkipSelfContained  Skip all self-contained artifacts (tiers 2 and 3).
.PARAMETER SelfContainedFormat 'both' (default), 'nupkg' (tier 2 only), or 'zip' (tier 3 only).

.EXAMPLE
  ./pack-all.ps1
.EXAMPLE
  ./pack-all.ps1 -Rids win-x64,linux-x64 -SelfContainedFormat nupkg
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$OutDir = "dist",
    [string[]]$Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"),
    [switch]$SkipTools,
    [switch]$SkipSelfContained,
    [ValidateSet("both", "nupkg", "zip")]
    [string]$SelfContainedFormat = "both"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$core = Join-Path $root "McpOrchestrator/McpOrchestrator.csproj"
$out  = Join-Path $root $OutDir
$work = Join-Path $out "_work"

function Invoke-Dotnet([string[]]$DotnetArgs, [string]$What) {
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

# Fresh output directory.
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null

# --- Tier 1: portable framework-dependent tool package ---
if (-not $SkipTools) {
    Write-Host "==> Tier 1: portable tool package..." -ForegroundColor Cyan
    Invoke-Dotnet @('pack', $core, '-c', 'Release', "-p:Version=$Version", '-o', $out, '--nologo', '-v', 'quiet') 'pack core'
}

# --- Tiers 2 & 3: self-contained, per RID ---
if (-not $SkipSelfContained) {
    $doNupkg = $SelfContainedFormat -in @('both', 'nupkg')
    $doZip   = $SelfContainedFormat -in @('both', 'zip')

    foreach ($rid in $Rids) {
        Write-Host "==> Self-contained for $rid..." -ForegroundColor Cyan

        # Tier 2: self-contained tool package (per-RID package id so a feed can host all platforms).
        if ($doNupkg) {
            Invoke-Dotnet @(
                'pack', $core, '-c', 'Release', '-r', $rid, '-p:SelfContained=true',
                "-p:PackRid=$rid", "-p:Version=$Version", '-o', $out, '--nologo', '-v', 'quiet'
            ) "sc tool core ($rid)"
        }

        # Tier 3: self-contained zip (no .NET required at all) — single-file, compressed.
        if ($doZip) {
            $coreDir = Join-Path $work "core-$rid"
            Invoke-Dotnet @(
                'publish', $core, '-c', 'Release', '-r', $rid, '--self-contained', 'true',
                '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none',
                "-p:Version=$Version", '-o', $coreDir, '--nologo', '-v', 'quiet'
            ) "publish core zip ($rid)"
            Compress-Archive -Path (Join-Path $coreDir '*') -Force `
                -DestinationPath (Join-Path $out "McpOrchestrator-$Version-$rid-selfcontained.zip")
        }
    }

    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}

# Summary.
Write-Host "`n==> Artifacts in $out :" -ForegroundColor Green
Get-ChildItem $out -File | Sort-Object Name | ForEach-Object {
    "{0,8:N1} MB   {1}" -f ($_.Length / 1MB), $_.Name
}
