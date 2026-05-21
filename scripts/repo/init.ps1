# This script installs all the needed items so agents can 
# work with the repo.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")

# Get OS
$os = $env:OS


# Windows Commands
if ($os -like "*Windows*") {

    # Installs use scoop.
    scoop install ripgrep
    scoop install grep

}


# Linux Commands
if ($os -like "*Linux*") {

    # Installs use apt.
    sudo apt update
    sudo apt install -y ripgrep grep

}

# Install local Node.js dependencies (includes VitePress from devDependencies).
$npmCommand = Get-Command npm -ErrorAction SilentlyContinue
if (-not $npmCommand) {
    throw "npm was not found on PATH. Install Node.js and npm, then rerun scripts/repo/init.ps1."
}

Push-Location $repoRoot
try {
    if (Test-Path (Join-Path $repoRoot "package-lock.json")) {
        npm ci
    }
    else {
        npm install
    }
}
finally {
    Pop-Location
}