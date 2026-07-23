Set-StrictMode -Version Latest

function Get-BotNexusValidationModeEnvironment {
    [CmdletBinding()]
    param()

    $values = [ordered]@{}
    foreach ($scope in @('Process', 'User', 'Machine')) {
        try {
            $values[$scope] = [Environment]::GetEnvironmentVariable('BOTNEXUS_VALIDATION_MODE', $scope)
        }
        catch [PlatformNotSupportedException] {
            $values[$scope] = $null
        }
    }
    return $values
}

function Resolve-BotNexusValidationMode {
    [CmdletBinding()]
    param(
        [string]$RequestedMode,
        [switch]$LocalFallback,
        [System.Collections.IDictionary]$EnvironmentValues = (Get-BotNexusValidationModeEnvironment)
    )

    if ($LocalFallback -and -not [string]::IsNullOrWhiteSpace($RequestedMode)) {
        throw '-LocalFallback cannot be combined with -ValidationMode.'
    }

    if ($LocalFallback) { return 'local' }

    $candidate = $RequestedMode
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        foreach ($scope in @('Process', 'User', 'Machine')) {
            if ($EnvironmentValues.Contains($scope) -and -not [string]::IsNullOrWhiteSpace([string]$EnvironmentValues[$scope])) {
                $candidate = [string]$EnvironmentValues[$scope]
                break
            }
        }
    }

    # Preserve the previous hook escape hatch while callers migrate to the named selector.
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        foreach ($scope in @('Process', 'User', 'Machine')) {
            try { $legacy = [Environment]::GetEnvironmentVariable('BOTNEXUS_VALIDATION_LOCAL_FALLBACK', $scope) }
            catch [PlatformNotSupportedException] { $legacy = $null }
            if ($legacy -eq '1') { return 'local' }
        }
    }

    if ([string]::IsNullOrWhiteSpace($candidate)) { return 'local' }
    $normalized = $candidate.Trim().ToLowerInvariant()
    if ($normalized -notin @('local', 'remote')) {
        # Do not include the supplied value: environment content can be sensitive.
        throw 'BOTNEXUS_VALIDATION_MODE must be either local or remote.'
    }
    return $normalized
}
