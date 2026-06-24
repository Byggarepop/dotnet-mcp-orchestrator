<#
.SYNOPSIS
  Packs McpOrchestrator into a local folder feed so an installed MCP host always runs the
  latest local code — without publishing to nuget.org.

.DESCRIPTION
  The package is always versioned 9.9.9-dev: it can never collide with a published version, so a
  pinned --version in your host config never serves a stale public package, and that config never
  needs touching when the real csproj <Version> bumps.

  Pair this with an mcpServers/servers entry that runs the tool from the local feed (--source
  replaces all NuGet sources, so startup never touches nuget.org):

    "command": "dotnet",
    "args": ["tool", "execute", "McpOrchestrator",
             "--version", "9.9.9-dev",
             "--source", "C:\\Users\\sebas\\Projects\\MCP\\nupkg\\local-feed",
             "--yes"],
    "env": { "MCP_ORCHESTRATOR_CONFIG": "C:\\path\\to\\orchestrator.config.json" }

  After running this script, restart Claude Code (MCP servers load at session start) to pick up the
  new build. The same installed tool also exposes the `init` and `profile` subcommands.
#>
[CmdletBinding()]
param(
    [string]$Version = "9.9.9-dev"
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$feedDir  = Join-Path $repoRoot "nupkg\local-feed"
$csproj   = Join-Path $repoRoot "McpOrchestrator\McpOrchestrator.csproj"

New-Item -ItemType Directory -Force $feedDir | Out-Null

Write-Host "Packing $csproj as $Version ..." -ForegroundColor Cyan
dotnet pack $csproj -c Release -p:Version=$Version -o $feedDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed with exit code $LASTEXITCODE" }

# `dotnet tool execute` caches the extracted package in the NuGet global packages folder; the same
# version is never re-fetched, so the previous dev build must be evicted for the new one to load.
$cacheDir = Join-Path $env:USERPROFILE ".nuget\packages\mcporchestrator\$Version"
if (Test-Path $cacheDir) {
    try {
        Remove-Item -Recurse -Force $cacheDir -ErrorAction Stop
        Write-Host "Cleared cached $Version from the NuGet packages folder." -ForegroundColor DarkGray
    } catch {
        Write-Warning "Could not clear $cacheDir - a running MCP server is likely locking it."
        Write-Warning "Close all Claude Code sessions using the dev server, then re-run this script."
        exit 1
    }
}

Write-Host "Done. Local feed: $feedDir" -ForegroundColor Green
Write-Host "Restart Claude Code to load the new build." -ForegroundColor Green
