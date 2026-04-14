#!/usr/bin/env pwsh
# Deploy extension build output to ~/.botnexus/extensions/ for runtime discovery.

[CmdletBinding()]
param(
    [Parameter()]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:USERPROFILE ".botnexus" "extensions"

$extensions = @(
    @{ Id = "botnexus-skills";  Src = "extensions/skills/BotNexus.Extensions.Skills" },
    @{ Id = "botnexus-exec";    Src = "extensions/tools/exec/BotNexus.Extensions.ExecTool" },
    @{ Id = "botnexus-process"; Src = "extensions/tools/process/BotNexus.Extensions.ProcessTool" },
    @{ Id = "botnexus-mcp";     Src = "extensions/mcp/BotNexus.Extensions.Mcp" },
    @{ Id = "botnexus-mcp-invoke"; Src = "extensions/mcp-invoke/BotNexus.Extensions.McpInvoke" },
    @{ Id = "botnexus-web";     Src = "extensions/web/BotNexus.Extensions.WebTools" }
)

foreach ($ext in $extensions) {
    $srcDir = Join-Path $repoRoot $ext.Src "bin" $Configuration "net10.0"
    $manifest = Join-Path $repoRoot $ext.Src "botnexus-extension.json"
    $extDest = Join-Path $dest $ext.Id

    if (-not (Test-Path $srcDir)) {
        Write-Warning "Build output not found: $srcDir — run dotnet build first."
        continue
    }

    New-Item -ItemType Directory -Path $extDest -Force | Out-Null
    Copy-Item "$srcDir\*" $extDest -Recurse -Force
    if (Test-Path $manifest) {
        Copy-Item $manifest $extDest -Force
    }
    Write-Host "✅ Deployed $($ext.Id) → $extDest"
}
