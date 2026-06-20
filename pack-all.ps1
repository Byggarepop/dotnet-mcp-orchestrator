<#
.SYNOPSIS
  Builds every distributable for McpOrchestrator into ./dist.

.DESCRIPTION
  Produces two kinds of artifacts:

   1. Portable .NET tool packages (framework-dependent, need .NET installed; one package serves
      all platforms):
        McpOrchestrator.<version>.nupkg            (core,  ~1.4 MB)
        McpOrchestrator.LocalLlm.<version>.nupkg   (+ local LLM, ~50 MB)

   2. Self-contained per-platform builds (no .NET install required), named with the runtime id
      (OS + architecture) and type:
        McpOrchestrator-<version>-<rid>-selfcontained.zip            (core, single-file)
        McpOrchestrator.LocalLlm-<version>-<rid>-selfcontained.zip   (+ local LLM)

  RIDs cover win/linux/osx on x64 and arm64. Cross-RID self-contained builds work from any OS.

.PARAMETER Version
  Package version (default 0.1.0).

.PARAMETER OutDir
  Output directory (default ./dist).

.PARAMETER Rids
  Runtime identifiers to build self-contained artifacts for.

.PARAMETER SkipTools
  Skip the portable .nupkg tool packages.

.PARAMETER SkipSelfContained
  Skip the self-contained per-RID zips.

.EXAMPLE
  ./pack-all.ps1
.EXAMPLE
  ./pack-all.ps1 -Rids win-x64,linux-x64
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$OutDir = "dist",
    [string[]]$Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"),
    [switch]$SkipTools,
    [switch]$SkipSelfContained
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$core = Join-Path $root "McpOrchestrator/McpOrchestrator.csproj"
$llm  = Join-Path $root "McpOrchestrator.LocalLlm/McpOrchestrator.LocalLlm.csproj"
$out  = Join-Path $root $OutDir
$work = Join-Path $out "_work"

function Invoke-Dotnet([string[]]$DotnetArgs, [string]$What) {
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

# Fresh output directory.
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 1) Portable framework-dependent tool packages.
if (-not $SkipTools) {
    Write-Host "==> Packing portable tool packages..." -ForegroundColor Cyan
    Invoke-Dotnet @('pack', $core, '-c', 'Release', "-p:Version=$Version", '-o', $out, '--nologo', '-v', 'quiet') 'pack core'
    Invoke-Dotnet @('pack', $llm,  '-c', 'Release', "-p:Version=$Version", '-o', $out, '--nologo', '-v', 'quiet') 'pack LocalLlm'
}

# 2) Self-contained per-RID builds.
if (-not $SkipSelfContained) {
    foreach ($rid in $Rids) {
        Write-Host "==> Self-contained build for $rid..." -ForegroundColor Cyan

        # Core: single-file, compressed, no pdb -> a single executable.
        $coreDir = Join-Path $work "core-$rid"
        Invoke-Dotnet @(
            'publish', $core, '-c', 'Release', '-r', $rid, '--self-contained', 'true',
            '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none',
            "-p:Version=$Version", '-o', $coreDir, '--nologo', '-v', 'quiet'
        ) "publish core ($rid)"
        Compress-Archive -Path (Join-Path $coreDir '*') -Force `
            -DestinationPath (Join-Path $out "McpOrchestrator-$Version-$rid-selfcontained.zip")

        # LocalLlm: self-contained folder (carries the platform's native llama.cpp libs).
        $llmDir = Join-Path $work "llm-$rid"
        Invoke-Dotnet @(
            'publish', $llm, '-c', 'Release', '-r', $rid, '--self-contained', 'true',
            '-p:DebugType=none', "-p:Version=$Version", '-o', $llmDir, '--nologo', '-v', 'quiet'
        ) "publish LocalLlm ($rid)"
        Compress-Archive -Path (Join-Path $llmDir '*') -Force `
            -DestinationPath (Join-Path $out "McpOrchestrator.LocalLlm-$Version-$rid-selfcontained.zip")
    }
    Remove-Item -Recurse -Force $work
}

# Summary.
Write-Host "`n==> Artifacts in $out :" -ForegroundColor Green
Get-ChildItem $out -File | Sort-Object Name | ForEach-Object {
    "{0,8:N1} MB   {1}" -f ($_.Length / 1MB), $_.Name
}
