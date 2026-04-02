[CmdletBinding()]
param(
    [string]$InstallPath = (Join-Path $HOME ".botnexus\app"),
    [string]$PackagesPath = (Join-Path (Split-Path -Path $PSScriptRoot -Parent) "artifacts")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-InstallTarget {
    param(
        [Parameter(Mandatory = $true)][string]$PackageId,
        [Parameter(Mandatory = $true)][string]$InstallRoot
    )

    if ($PackageId -eq "BotNexus.Gateway") {
        return [pscustomobject]@{ Kind = "gateway"; TargetPath = (Join-Path $InstallRoot "gateway") }
    }

    if ($PackageId -eq "BotNexus.Cli") {
        return [pscustomobject]@{ Kind = "cli"; TargetPath = (Join-Path $InstallRoot "cli") }
    }

    if ($PackageId -match "^BotNexus\.(?<type>Providers|Channels|Tools)\.(?<name>.+)$") {
        $type = $Matches.type.ToLowerInvariant()
        $name = $Matches.name.ToLowerInvariant()
        return [pscustomobject]@{
            Kind       = "extension"
            Type       = $type
            Name       = $name
            TargetPath = (Join-Path $InstallRoot ("extensions\{0}\{1}" -f $type, $name))
        }
    }

    throw "Unknown package naming pattern: $PackageId"
}

function Test-IsNuGetMetadataEntry {
    param([Parameter(Mandatory = $true)][string]$EntryPath)

    $normalized = $EntryPath.Replace('\', '/')
    if ($normalized -eq "[Content_Types].xml") { return $true }
    if ($normalized.StartsWith("_rels/")) { return $true }
    if ($normalized.StartsWith("package/")) { return $true }
    if ($normalized.EndsWith(".nuspec")) { return $true }
    return $false
}

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$resolvedInstallPath = [System.IO.Path]::GetFullPath($InstallPath)
$resolvedPackagesPath = [System.IO.Path]::GetFullPath($PackagesPath)

if (-not (Test-Path -LiteralPath $resolvedPackagesPath)) {
    throw "Packages directory not found: $resolvedPackagesPath"
}

if (-not (Test-Path -LiteralPath $resolvedInstallPath)) {
    New-Item -ItemType Directory -Path $resolvedInstallPath | Out-Null
}

$packageFiles = Get-ChildItem -LiteralPath $resolvedPackagesPath -Filter "*.nupkg" | Sort-Object Name
if (-not $packageFiles) {
    throw "No .nupkg files found in $resolvedPackagesPath"
}

$installed = New-Object System.Collections.Generic.List[object]

foreach ($package in $packageFiles) {
    $packageId = $package.BaseName
    $target = Get-InstallTarget -PackageId $packageId -InstallRoot $resolvedInstallPath
    $targetPath = [string]$target.TargetPath

    if (Test-Path -LiteralPath $targetPath) {
        Remove-Item -LiteralPath $targetPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $targetPath -Force | Out-Null

    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrWhiteSpace($entry.Name)) {
                continue
            }

            $entryPath = $entry.FullName.Replace('/', '\')
            if (Test-IsNuGetMetadataEntry -EntryPath $entryPath) {
                continue
            }

            $destinationPath = Join-Path $targetPath $entryPath
            $destinationDir = Split-Path -Path $destinationPath -Parent
            if (-not (Test-Path -LiteralPath $destinationDir)) {
                New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
            }

            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destinationPath, $true)
        }
    }
    finally {
        $archive.Dispose()
    }

    $installed.Add([pscustomobject]@{
            Package = $package.Name
            Target  = $targetPath
        })
}

$commitHash = "unknown"
try {
    $hash = git -C $repoRoot rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($hash)) {
        $commitHash = $hash.Trim()
    }
}
catch {
}

$versionPayload = [pscustomobject]@{
    InstalledAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    Commit         = $commitHash
    InstallPath    = $resolvedInstallPath
    Packages       = @($installed | ForEach-Object { $_.Package })
}
$versionPath = Join-Path $resolvedInstallPath "version.json"
$versionPayload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $versionPath -Encoding UTF8

$configPath = Join-Path $HOME ".botnexus\config.json"
if (Test-Path -LiteralPath $configPath) {
    try {
        $configRaw = Get-Content -LiteralPath $configPath -Raw
        $configJson = $configRaw | ConvertFrom-Json
        $extensionsPath = Join-Path $resolvedInstallPath "extensions"

        if ($null -ne $configJson.BotNexus) {
            $configJson.BotNexus | Add-Member -NotePropertyName ExtensionsPath -NotePropertyValue $extensionsPath -Force
        }

        $configJson | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding UTF8
        Write-Host "Updated ExtensionsPath in $configPath"
    }
    catch {
        Write-Warning "Could not update $configPath`: $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Installed $($installed.Count) package(s) to $resolvedInstallPath"
$installed | Sort-Object Package | Format-Table -AutoSize
Write-Host "version.json written to $versionPath"
