[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'FirewallLeaseProgram.ps1')

$failures = [Collections.Generic.List[string]]::new()
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { $failures.Add($Message) } }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$cliTestsProject = Join-Path $repoRoot 'tests/gateway/BotNexus.Cli.Tests/BotNexus.Cli.Tests.csproj'
$cliOutputDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot 'tests/gateway/BotNexus.Cli.Tests/bin/Debug/net10.0'))

# --- AC4 FIRST (the safety clause): the lease must never widen -------------
# A lease is a mutation of host firewall state. An enumeration that returns
# binaries from OUTSIDE the project's own output directory must be discarded,
# not leased. Otherwise a stray predicate or a symlinked probe directory could
# open holes for arbitrary executables on the machine.
$widePredicate = {
    param($directory)
    @(
        (Join-Path $directory 'testhost.exe'),
        (Join-Path $directory 'BotNexus.Cli.exe'),
        # deeper than the output directory itself
        (Join-Path $directory 'runtimes/win-x64/native/probe.exe'),
        # entirely outside the repo
        'C:\Windows\System32\cmd.exe',
        # a sibling project's output
        (Join-Path $repoRoot 'tests/gateway/BotNexus.Gateway.Tests/bin/Debug/net10.0/testhost.exe')
    )
}
$narrowed = @(Get-LeasedProgramPath -ProjectPath $cliTestsProject -GetOutputExecutable $widePredicate)
Assert-True (-not ($narrowed -contains 'C:\Windows\System32\cmd.exe')) 'AC4: a binary outside the project output directory must never be leased.'
foreach ($leased in $narrowed) {
    $parent = Split-Path -Parent $leased
    Assert-True ($parent.TrimEnd('\').Equals($cliOutputDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) `
        "AC4: leased program must live directly in the project's own output directory: $leased"
}
Assert-True ($narrowed.Count -eq 2) 'AC4: exactly the two in-directory executables may be leased from the wide predicate.'

# --- AC2: the leased set is DERIVED FROM BUILD OUTPUT ----------------------
# The literal `testhost.exe` is no longer the definition of the set. Seed a
# realistic output listing and require BotNexus.Cli.exe by name - it is the
# binary CliTestFixture.cs and CrossProcessConfigWriteTests.cs actually spawn.
$builtOutput = {
    param($directory)
    @(
        (Join-Path $directory 'testhost.exe'),
        (Join-Path $directory 'BotNexus.Cli.exe')
    )
}
$derived = @(Get-LeasedProgramPath -ProjectPath $cliTestsProject -GetOutputExecutable $builtOutput)
$leafNames = @($derived | ForEach-Object { Split-Path $_ -Leaf })
Assert-True ($leafNames -contains 'BotNexus.Cli.exe') 'AC2: BotNexus.Cli.exe must be in the derived lease set for BotNexus.Cli.Tests.'
Assert-True ($leafNames -contains 'testhost.exe') 'AC2: testhost.exe must remain in the derived lease set.'
Assert-True ($derived -contains (Join-Path $cliOutputDirectory 'BotNexus.Cli.exe')) 'AC2: the derived BotNexus.Cli.exe path must be the project output path.'

# Non-vacuity for AC2: with an output directory containing ONLY testhost.exe,
# the CLI assertion above must be unsatisfiable. This proves the assertion is
# reading the enumeration and not a hard-coded list.
$testhostOnly = { param($directory) @((Join-Path $directory 'testhost.exe')) }
$derivedThin = @(Get-LeasedProgramPath -ProjectPath $cliTestsProject -GetOutputExecutable $testhostOnly)
$thinLeaves = @($derivedThin | ForEach-Object { Split-Path $_ -Leaf })
Assert-True (-not ($thinLeaves -contains 'BotNexus.Cli.exe')) 'Non-vacuity: with only testhost.exe built, the derived set must not contain BotNexus.Cli.exe.'

# --- sad path: nothing built yet falls back to the pre-#2774 floor ---------
# A first run before any build must still lease something, or the change would
# regress the very prompt it exists to prevent.
$nothingBuilt = { param($directory) @() }
$fallback = @(Get-LeasedProgramPath -ProjectPath $cliTestsProject -GetOutputExecutable $nothingBuilt)
Assert-True ($fallback.Count -eq 1) 'Fallback: an unbuilt project must still yield exactly one lease path.'
Assert-True ((Split-Path $fallback[0] -Leaf) -eq 'testhost.exe') 'Fallback: an unbuilt project falls back to the composed testhost.exe path.'
Assert-True ($fallback[0] -eq (Join-Path $cliOutputDirectory 'testhost.exe')) 'Fallback: the composed path must be the project output testhost.exe.'

# --- multiple projects, de-duplication, and empty/null inputs --------------
$gatewayTestsProject = Join-Path $repoRoot 'tests/gateway/BotNexus.Gateway.Tests/BotNexus.Gateway.Tests.csproj'
$multi = @(Get-LeasedProgramPath -ProjectPath @($cliTestsProject, $gatewayTestsProject, $cliTestsProject) -GetOutputExecutable $builtOutput)
Assert-True ($multi.Count -eq 4) 'Two distinct projects with two executables each yield four leases, de-duplicated.'
$distinct = @($multi | Sort-Object -Unique)
Assert-True ($distinct.Count -eq $multi.Count) 'Derived lease paths must be de-duplicated case-insensitively.'

Assert-True (@(Get-LeasedProgramPath -ProjectPath @() -GetOutputExecutable $builtOutput).Count -eq 0) 'No projects yields no lease paths.'
Assert-True (@(Get-LeasedProgramPath -ProjectPath $null -GetOutputExecutable $builtOutput).Count -eq 0) 'A null project list yields no lease paths.'
Assert-True (@(Get-LeasedProgramPath -ProjectPath @('', '   ') -GetOutputExecutable $builtOutput).Count -eq 0) 'Blank project paths are ignored.'

# --- configuration/target framework are honoured, not assumed --------------
$release = @(Get-LeasedProgramPath -ProjectPath $cliTestsProject -Configuration 'Release' -TargetFramework 'net10.0' -GetOutputExecutable $builtOutput)
foreach ($program in $release) {
    Assert-True ($program -like '*\bin\Release\net10.0\*') "Configuration must be honoured in the derived path: $program"
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    Write-Host "FirewallLeaseProgram.Tests.ps1: $($failures.Count) failure(s)."
    exit 1
}

Write-Host 'FirewallLeaseProgram.Tests.ps1: all assertions passed.'
exit 0
