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

function Get-BotNexusLegacyFallbackEnvironment {
    [CmdletBinding()]
    param()

    $values = [ordered]@{}
    foreach ($scope in @('Process', 'User', 'Machine')) {
        try {
            $values[$scope] = [Environment]::GetEnvironmentVariable('BOTNEXUS_VALIDATION_LOCAL_FALLBACK', $scope)
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
        [System.Collections.IDictionary]$EnvironmentValues = (Get-BotNexusValidationModeEnvironment),
        # Injected alongside $EnvironmentValues so callers (notably the self-test) can be
        # made fully independent of ambient Process/User/Machine configuration (#2400).
        [System.Collections.IDictionary]$LegacyFallbackValues = (Get-BotNexusLegacyFallbackEnvironment)
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
            if ($LegacyFallbackValues.Contains($scope) -and [string]$LegacyFallbackValues[$scope] -eq '1') { return 'local' }
        }
    }

    # No explicit selection anywhere: default to REMOTE.
    #
    # This used to return 'local', which made the banned path the path you got by doing
    # nothing. On 2026-08-06 local gates leaked three orphan gateway processes - parent dies,
    # child survives - two of which ran for 30+ hours. They starved the live gateway until the
    # portal would not load, and because every gateway opens the shared cron store they claimed
    # scheduled jobs belonging to the real one and failed them. Jon banned local validation on
    # the development host that day (#2158).
    #
    # A default is an instruction. Defaulting to the mode that damages the host and then
    # documenting "do not use local" puts the code and the rule in direct contradiction, and the
    # code wins every time nobody is reading. Local remains fully available, but it must now be
    # ASKED FOR - via -ValidationMode local, -LocalFallback, or BOTNEXUS_VALIDATION_MODE - so
    # that using it is a deliberate, attributable choice rather than an accident.
    if ([string]::IsNullOrWhiteSpace($candidate)) { return 'remote' }
    $normalized = $candidate.Trim().ToLowerInvariant()
    if ($normalized -notin @('local', 'remote')) {
        # Do not include the supplied value: environment content can be sensitive.
        throw 'BOTNEXUS_VALIDATION_MODE must be either local or remote.'
    }
    return $normalized
}
