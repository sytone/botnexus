Set-StrictMode -Version Latest

function Assert-RunnerReleasePath {
    <#
    .SYNOPSIS
    Checks a local absolute path and every existing ancestor for links.
    .DESCRIPTION
    Rejects provider, network and device paths. Checks lexical traversal before and
    after normalization. These checks do not provide an atomic no-follow filesystem open.
    .PARAMETER Path
    Local absolute filesystem path to inspect.
    .EXAMPLE
    Assert-RunnerReleasePath -Path /operator/release.json
    .OUTPUTS
    System.String. Normalized absolute path.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.Contains('::') -or
        $Path -match '^[\\/]{2}' -or $Path -match '[\x00-\x1f]') {
        throw 'Use an absolute local filesystem path, not a provider, network or device path.'
    }
    if ($IsWindows -and $Path.Substring(2).Contains(':')) {
        throw 'Alternate data streams are not supported.'
    }
    $full = [IO.Path]::GetFullPath($Path)
    $anchor = [IO.Path]::GetPathRoot($Path)
    $walk = $anchor
    $segments = $Path.Substring($anchor.Length) -split '[\\/]'
    foreach ($segment in $segments) {
        if (-not $segment -or $segment -eq '.') {
            continue
        }
        $walk = [IO.Path]::GetFullPath([IO.Path]::Combine($walk, $segment))
        if ([IO.File]::Exists($walk) -or [IO.Directory]::Exists($walk)) {
            if (([IO.File]::GetAttributes($walk) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Linked paths or ancestors are not supported.'
            }
        }
    }
    $walk = $full
    while ($walk) {
        # GetAttributes also rejects inaccessible and broken-link paths rather than following them.
        if (([IO.File]::GetAttributes($walk) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Linked paths or ancestors are not supported.'
        }
        $parent = [IO.Directory]::GetParent($walk)
        $walk = if ($null -eq $parent) {
            $null
        }
        else {
            $parent.FullName
        }
    }
    return $full
}

function Get-RunnerReleaseInputDigest {
    <#
    .SYNOPSIS
    Hashes the fixed execution and toolchain input scope.
    .DESCRIPTION
    Enumerates without following links and sorts paths ordinally. Includes nonfixture
    buildtest infrastructure and repository PowerShell, MSBuild/project/runsettings policy,
    SDK/NuGet/tool selection, and diagnostic collector code. Excludes product source,
    build output and fixtures. Sparse synthetic roots must contain at least one input.
    Hash material starts with UTF8 botnexus-qualified-execution-inputs-v1 followed by NUL.
    Each ordinally sorted file contributes decimal UTF8 path byte length, colon, path,
    decimal normalized-content byte length, colon, content. Lengths use invariant culture.
    This is qualification-input identity, not image identity; compatible input changes
    may be externally requalified against the same immutable runner image.
    .PARAMETER Root
    Previously checked candidate directory.
    .EXAMPLE
    Get-RunnerReleaseInputDigest -Root /candidate
    .OUTPUTS
    System.String. Lowercase SHA256 execution-input identity, not a candidate snapshot proof.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Root)

    $paths = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $relative = [IO.Path]::GetRelativePath($Root, $entry).Replace('\', '/')
            if ($relative -match '(^|/)(\.git|bin|obj|node_modules|artifacts|\.affected)(/|$)' -or
                $relative -match '^infra/buildtest/(.*/)?tests(/|$)') {
                continue
            }
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Candidate contains a linked execution traversal path; qualification requires regular files.'
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
                continue
            }
            # This production entrypoint is invoked by strict/impacted runner modes.
            # Classify it explicitly before the case-insensitive fixture convention;
            # changing regex case sensitivity would silently lose it on another host.
            $productionEntrypoint = $relative -ieq 'scripts/repo/test-impacted.ps1'
            $fixture = -not $productionEntrypoint -and $relative -match '(^|/)(Test-[^/]+\.ps1|[^/]+\.(Tests|Mutation)\.ps1)$'
            $selected = $relative -match '^infra/buildtest/' -or
            ($relative -match '^scripts/repo/[^/]+\.ps(m1|1)$' -and -not $fixture) -or
            $relative -match '\.(props|targets|proj|csproj|runsettings)$' -or
            $relative -match '(^|/)(global\.json|nuget\.config)$' -or
            $relative -in @('dotnet-tools.json', '.config/dotnet-tools.json') -or
            $relative -match '^tools/BotNexus\.TestEventCollector/.*\.(cs|json)$'
            if ($selected) {
                $paths.Add($relative)
            }
        }
    }
    if ($paths.Count -eq 0) {
        throw 'No execution inputs found; cannot qualify an empty candidate.'
    }
    $paths.Sort([StringComparer]::Ordinal)
    $portable = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        $hash.AppendData($encoding.GetBytes("botnexus-qualified-execution-inputs-v1`0"))
        foreach ($relative in $paths) {
            if (-not $portable.Add($relative)) {
                throw 'Case-colliding execution input paths are not portable.'
            }
            $path = Join-Path $Root $relative
            # Recheck just before reading; this narrows but cannot eliminate rename races.
            Assert-RunnerReleasePath -Path $path | Out-Null
            $content = $encoding.GetString([IO.File]::ReadAllBytes($path)).TrimStart([char]0xfeff).Replace("`r`n", "`n")
            $pathBytes = $encoding.GetBytes($relative)
            $contentBytes = $encoding.GetBytes($content)
            $hash.AppendData($encoding.GetBytes($pathBytes.Length.ToString([Globalization.CultureInfo]::InvariantCulture) + ':'))
            $hash.AppendData($pathBytes)
            $hash.AppendData($encoding.GetBytes($contentBytes.Length.ToString([Globalization.CultureInfo]::InvariantCulture) + ':'))
            $hash.AppendData($contentBytes)
        }
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Read-RunnerReleaseManifest {
    <#
    .SYNOPSIS
    Reads and validates one bounded external manifest document.
    .DESCRIPTION
    Reads at most 65537 bytes through one handle. Hashes exactly those bytes and uses
    strict UTF8 and System.Text.Json token types, rejecting duplicate or unknown keys.
    Trusts the coordinator-selected external profile and local shell. Does not enforce
    independent ACL protection or shell isolation, or confer release qualification.
    .PARAMETER Path
    Checked manifest file path.
    .EXAMPLE
    Read-RunnerReleaseManifest -Path /operator/release.json
    .OUTPUTS
    System.Management.Automation.PSCustomObject. Validated document and exact byte hash.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $buffer = [byte[]]::new(65537)
        $count = 0
        while ($count -lt $buffer.Length) {
            $read = $stream.Read($buffer, $count, $buffer.Length - $count)
            if ($read -eq 0) {
                break
            }
            $count += $read
        }
        if ($count -eq 0 -or $count -gt 65536) {
            throw 'Manifest must contain between 1 and 65536 bytes.'
        }
        $bytes = [byte[]]::new($count)
        [Array]::Copy($buffer, $bytes, $count)
    }
    finally {
        $stream.Dispose()
    }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 8
    $json = [System.Text.Json.JsonDocument]::Parse($text, $options)
    try {
        if ($json.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw 'Manifest must be one object.'
        }
        $required = @('schemaVersion', 'releaseId', 'image', 'approved', 'revoked', 'runnerInputDigest', 'modes', 'payloadVersion', 'evidenceVersion', 'capabilities')
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $document = @{}
        foreach ($property in $json.RootElement.EnumerateObject()) {
            if (-not $seen.Add($property.Name) -or $property.Name -cnotin $required) {
                throw 'Manifest has a duplicate, case-colliding or unknown property.'
            }
            $value = $property.Value
            switch -CaseSensitive ($property.Name) {
                { $_ -in @('schemaVersion', 'payloadVersion', 'evidenceVersion') } {
                    if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or $value.GetRawText() -cne '1') {
                        throw 'Only canonical integer contract version 1 is supported.'
                    }
                    $document[$property.Name] = 1
                }
                { $_ -in @('approved', 'revoked') } {
                    if ($value.ValueKind -notin @([System.Text.Json.JsonValueKind]::True, [System.Text.Json.JsonValueKind]::False)) {
                        throw 'Approval and revocation must be JSON booleans.'
                    }
                    $document[$property.Name] = $value.GetBoolean()
                }
                { $_ -in @('modes', 'capabilities') } {
                    if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $value.GetArrayLength() -lt 1 -or $value.GetArrayLength() -gt 6) {
                        throw 'Modes and capabilities must be nonempty bounded arrays.'
                    }
                    $items = [Collections.Generic.List[string]]::new()
                    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                    foreach ($element in $value.EnumerateArray()) {
                        if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                            throw 'Mode and capability values must be strings.'
                        }
                        $item = $element.GetString()
                        if ($item.Length -gt 256 -or -not $unique.Add($item)) {
                            throw 'Mode and capability values must be bounded and distinct.'
                        }
                        $items.Add($item)
                    }
                    $document[$property.Name] = $items.ToArray()
                }
                default {
                    $limit = if ($property.Name -ceq 'image') {
                        512
                    }
                    else {
                        256
                    }
                    if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or $value.GetString().Length -gt $limit) {
                        throw 'Manifest string has an invalid type or length.'
                    }
                    $document[$property.Name] = $value.GetString()
                }
            }
        }
        if ($seen.Count -ne $required.Count) {
            throw 'Manifest is missing required properties.'
        }
        return [pscustomobject]@{
            document = $document
            sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        }
    }
    finally {
        $json.Dispose()
    }
}

function Assert-RunnerReleaseCompatibility {
    <#
    .SYNOPSIS
    Requires an externally approved runner release compatible with the candidate execution inputs.
    .DESCRIPTION
    Performs read-only local validation, not deployment or authentication. Requires a
    regular external profile selected by the trusted coordinator and exact immutable
    provider image. The profile uses ordinary existing user-profile permissions.
    Trusts the coordinator and local shell; native peer tool restrictions do not provide
    OS or shell isolation or an operator-only ACL/nonreplacement guarantee. Filesystem
    checks reject observed links but are not atomic; keep profile and candidate paths stable.
    Each call rereads approval. Callers must recheck at launch, completion and receipt reuse.
    Input qualification is distinct from image identity. Compatible sender or receipt
    changes may be externally requalified against the same immutable image. This module
    never decides that an input change automatically requires rebuilding or redeployment.
    .PARAMETER ManifestPath
    Absolute coordinator-selected external profile path outside the candidate. No implicit discovery.
    .PARAMETER CandidateRoot
    Absolute candidate root; infrastructure inputs are hashed separately from application source.
    .PARAMETER DeployedImage
    Actual provider repository image pinned by lowercase SHA256 digest, not a tag or alias.
    .PARAMETER Mode
    Requested known validation mode; diagnostic additionally requires containment capability.
    .EXAMPLE
    Assert-RunnerReleaseCompatibility -ManifestPath /operator/release.json -CandidateRoot /candidate -DeployedImage $providerImage -Mode core
    .OUTPUTS
    System.Management.Automation.PSCustomObject. Runner release binding only, never source or test proof.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$CandidateRoot,
        [Parameter(Mandatory)][string]$DeployedImage,
        [Parameter(Mandatory)][string]$Mode
    )

    try {
        $root = Assert-RunnerReleasePath -Path $CandidateRoot
        if (-not [IO.Directory]::Exists($root)) {
            throw 'CandidateRoot must be a regular directory.'
        }
        $path = Assert-RunnerReleasePath -Path $ManifestPath
        $comparison = if ($IsWindows) {
            [StringComparison]::OrdinalIgnoreCase
        }
        else {
            [StringComparison]::Ordinal
        }
        $prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($path.Equals($root, $comparison) -or $path.StartsWith($prefix, $comparison)) {
            throw 'Operator manifest must be outside the candidate.'
        }
        $manifest = Read-RunnerReleaseManifest -Path $path
        $document = $manifest.document
        if (-not $document.approved -or $document.revoked) {
            throw 'Operator release is not approved or is revoked; obtain a current qualified release.'
        }
        if ($document.releaseId -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
            throw 'Release identity is not a bounded portable identifier.'
        }
        $imagePattern = '^[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]{1,5})?/[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*@sha256:[0-9a-f]{64}$'
        if ($document.image -cnotmatch $imagePattern -or $document.image -cne $DeployedImage) {
            throw 'Provider image must equal the approved immutable repository@sha256 digest.'
        }
        $knownModes = @('strict', 'impacted', 'full', 'core', 'playwright', 'diagnostic')
        if ($Mode -cnotin $knownModes -or $Mode -cnotin $document.modes) {
            throw 'Requested mode is unknown or not qualified by this release.'
        }
        foreach ($entry in $document.modes) {
            if ($entry -cnotin $knownModes) {
                throw 'Manifest contains an unknown mode.'
            }
        }
        foreach ($capability in $document.capabilities) {
            if ($capability -cnotin @('exact-source-v1', 'diagnostic-containment-v1')) {
                throw 'Manifest contains an unknown capability.'
            }
        }
        if ('exact-source-v1' -cnotin $document.capabilities -or
            ($Mode -ceq 'diagnostic' -and 'diagnostic-containment-v1' -cnotin $document.capabilities)) {
            throw 'Required source-proof or diagnostic-containment capability is missing.'
        }
        if ($document.runnerInputDigest -cnotmatch '^[0-9a-f]{64}$' -or
            $document.runnerInputDigest -cne (Get-RunnerReleaseInputDigest -Root $root)) {
            throw 'Execution inputs changed; external execution-contract qualification is required. A compatible change may be requalified against the same immutable image; rebuild or redeployment is not automatically required.'
        }
        return [pscustomobject][ordered]@{
            releaseId = $document.releaseId
            image = $document.image
            manifestSha256 = $manifest.sha256
            runnerInputDigest = $document.runnerInputDigest
            payloadVersion = $document.payloadVersion
            evidenceVersion = $document.evidenceVersion
            mode = $Mode
        }
    }
    catch {
        $record = [Management.Automation.ErrorRecord]::new(
            [InvalidOperationException]::new("Runner release compatibility failed: $($_.Exception.Message)", $_.Exception),
            'RunnerReleaseCompatibility', [Management.Automation.ErrorCategory]::InvalidData, $ManifestPath)
        $PSCmdlet.ThrowTerminatingError($record)
    }
}

function Assert-RunnerReleaseExecution {
    <#
    .SYNOPSIS
    Compares persisted launch evidence with revalidated current external approval.
    .DESCRIPTION
    Performs read-only comparison. The stored binding is not approval authority.
    Optional provider execution container must match the pinned image and have no
    command or args override. Callers supply the operator environment selector;
    this module never reads environment or candidate configuration.
    .PARAMETER Binding
    Persisted v2 launch or receipt release binding, never an expected approval document.
    .PARAMETER ManifestPath
    Operator-selected current external approval path; empty selectors fail closed.
    .PARAMETER CandidateRoot
    Current exact candidate root whose execution inputs must remain qualified.
    .PARAMETER DeployedImage
    Provider execution image for recovery, or recorded validated execution image for cache reuse.
    .PARAMETER Mode
    Required qualified execution mode, including noncertifying diagnostic.
    .PARAMETER ExecutionConfiguration
    Persisted policy identity. Only image-entrypoint-v1 without overrides is supported.
    .PARAMETER Container
    Optional singleton execution container from a trusted provider response.
    .EXAMPLE
    Assert-RunnerReleaseBinding -Binding $context.runnerRelease -ManifestPath $operatorPath -CandidateRoot $repo -DeployedImage $container.image -Mode core -ExecutionConfiguration image-entrypoint-v1 -Container $container
    .OUTPUTS
    System.Management.Automation.PSCustomObject. Independently validated current binding.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]$Binding,
        [AllowNull()][AllowEmptyString()][string]$ManifestPath,
        [Parameter(Mandatory)][string]$CandidateRoot,
        [AllowNull()][AllowEmptyString()][string]$DeployedImage,
        [Parameter(Mandatory)][string]$Mode,
        [AllowNull()][AllowEmptyString()][string]$ExecutionConfiguration,
        [AllowNull()]$Container
    )
    try {
        if (-not $Binding -or [string]::IsNullOrWhiteSpace($ManifestPath) -or
            [string]::IsNullOrWhiteSpace($DeployedImage) -or
            $ExecutionConfiguration -cne 'image-entrypoint-v1') {
            throw 'A v2 launch binding, current operator selector and image-entrypoint-v1 certification policy are required; legacy evidence cannot self-upgrade.'
        }
        $current = Assert-RunnerReleaseCompatibility -ManifestPath $ManifestPath -CandidateRoot $CandidateRoot -DeployedImage $DeployedImage -Mode $Mode
        $fields = @('releaseId', 'image', 'manifestSha256', 'runnerInputDigest', 'payloadVersion', 'evidenceVersion', 'mode')
        if (@($Binding.PSObject.Properties).Count -ne $fields.Count) {
            throw 'Persisted release binding has an invalid field set.'
        }
        foreach ($field in $fields) {
            $property = @($Binding.PSObject.Properties | Where-Object Name -CEQ $field)
            if ($property.Count -ne 1 -or $null -eq $property[0].Value) {
                throw 'Persisted release binding is missing a canonical field.'
            }
            $value = $property[0].Value
            if ($field -in @('payloadVersion', 'evidenceVersion')) {
                if (($value -isnot [int] -and $value -isnot [long]) -or $value -ne $current.$field) {
                    throw 'Persisted release contract version differs from current qualification.'
                }
            }
            elseif ($value -isnot [string] -or $value -cne $current.$field) {
                throw 'Persisted release binding differs from current operator qualification.'
            }
        }
        if ($null -ne $Container) {
            if (-not $Container.PSObject.Properties['image'] -or $Container.image -isnot [string] -or $Container.image -cne $Binding.image) {
                throw 'Provider execution image differs from the pinned launch image.'
            }
            foreach ($name in @('command', 'args')) {
                $override = $Container.PSObject.Properties[$name]
                if ($null -ne $override -and $null -ne $override.Value) {
                    if ($override.Value -isnot [Array] -or $override.Value.Count -ne 0) {
                        throw 'Provider command or args override is not approved by image-entrypoint-v1.'
                    }
                }
            }
        }
        return $current
    }
    catch {
        $record = [Management.Automation.ErrorRecord]::new(
            [InvalidOperationException]::new("Runner release binding failed: $($_.Exception.Message)", $_.Exception),
            'RunnerReleaseCompatibilityBinding', [Management.Automation.ErrorCategory]::InvalidData, $Binding)
        $PSCmdlet.ThrowTerminatingError($record)
    }
}

function Assert-RunnerReleaseBinding {
    <#
    .SYNOPSIS
    Requires a qualified non-diagnostic receipt binding.
    .DESCRIPTION
    Uses the shared read-only execution validator while refusing diagnostic certification.
    Does not read environment or trust persisted binding as operator authority.
    .PARAMETER Binding
    Persisted v2 launch or receipt binding.
    .PARAMETER ManifestPath
    Current operator-selected external manifest path.
    .PARAMETER CandidateRoot
    Exact candidate root.
    .PARAMETER DeployedImage
    Observed or previously validated immutable execution image.
    .PARAMETER Mode
    Required non-diagnostic certification mode.
    .PARAMETER ExecutionConfiguration
    Only image-entrypoint-v1 is supported.
    .PARAMETER Container
    Optional singleton trusted provider execution container.
    .EXAMPLE
    Assert-RunnerReleaseBinding -Binding $binding -ManifestPath $path -CandidateRoot $repo -DeployedImage $image -Mode core -ExecutionConfiguration image-entrypoint-v1
    .OUTPUTS
    System.Management.Automation.PSCustomObject. Independently validated binding.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]$Binding,
        [AllowNull()][AllowEmptyString()][string]$ManifestPath,
        [Parameter(Mandatory)][string]$CandidateRoot,
        [AllowNull()][AllowEmptyString()][string]$DeployedImage,
        [Parameter(Mandatory)][string]$Mode,
        [AllowNull()][AllowEmptyString()][string]$ExecutionConfiguration,
        [AllowNull()]$Container
    )
    if ($Mode -ceq 'diagnostic') {
        $record = [Management.Automation.ErrorRecord]::new(
            [InvalidOperationException]::new('Diagnostic cannot certify validation receipts.'),
            'RunnerReleaseCompatibilityMode', [Management.Automation.ErrorCategory]::InvalidData, $Mode)
        $PSCmdlet.ThrowTerminatingError($record)
    }
    Assert-RunnerReleaseExecution @PSBoundParameters
}

Export-ModuleMember -Function Assert-RunnerReleaseCompatibility, Assert-RunnerReleaseBinding, Assert-RunnerReleaseExecution
