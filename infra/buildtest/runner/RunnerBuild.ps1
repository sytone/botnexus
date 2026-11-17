<#
.SYNOPSIS
    Runs independent runner build processes concurrently while preserving separate diagnostics.
.DESCRIPTION
    Debug and Release builds use configuration-specific output and intermediate directories, so
    the remote runner can start both after the single restore. Every child is awaited even when a
    sibling fails; callers receive all exit codes and decide how to fail the gate.
#>

function Invoke-ParallelRunnerProcesses {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][System.Collections.IDictionary[]] $Processes
    )

    $running = @()
    try {
        foreach ($spec in $Processes) {
            foreach ($required in @('Name', 'FilePath', 'ArgumentList', 'LogPath')) {
                if (-not $spec.Contains($required)) {
                    throw "Parallel runner process specification is missing '$required'."
                }
            }

            $stdout = "$($spec.LogPath).stdout"
            $stderr = "$($spec.LogPath).stderr"
            $startedAt = [DateTimeOffset]::UtcNow
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $process = Start-Process `
                -FilePath $spec.FilePath `
                -ArgumentList @($spec.ArgumentList) `
                -NoNewWindow `
                -PassThru `
                -RedirectStandardOutput $stdout `
                -RedirectStandardError $stderr

            $running += [pscustomobject]@{
                Name = [string]$spec.Name
                LogPath = [string]$spec.LogPath
                StandardOutputPath = $stdout
                StandardErrorPath = $stderr
                Process = $process
                StartedAt = $startedAt
                Stopwatch = $stopwatch
            }
        }

        $pending = @($running)
        while ($pending.Count -gt 0) {
            Wait-Process -InputObject @($pending.Process) -Any
            foreach ($child in @($pending | Where-Object { $_.Process.HasExited })) {
                $child.Stopwatch.Stop()
            }
            $pending = @($pending | Where-Object { -not $_.Process.HasExited })
        }

        $results = foreach ($child in $running) {
            $child.Process.WaitForExit()

            $parts = foreach ($path in @($child.StandardOutputPath, $child.StandardErrorPath)) {
                if (Test-Path -LiteralPath $path) {
                    Get-Content -LiteralPath $path -Raw -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
                }
            }
            Set-Content -LiteralPath $child.LogPath -Value (@($parts) -join [Environment]::NewLine)

            [pscustomobject]@{
                Name = $child.Name
                ExitCode = $child.Process.ExitCode
                StartedAt = $child.StartedAt
                ElapsedSeconds = $child.Stopwatch.Elapsed.TotalSeconds
                LogPath = $child.LogPath
            }
        }

        return @($results)
    }
    catch {
        foreach ($child in $running) {
            if (-not $child.Process.HasExited) {
                try { $child.Process.Kill($true) } catch { }
                try { $child.Process.WaitForExit(30000) | Out-Null } catch { }
            }
        }
        throw
    }
}