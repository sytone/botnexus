using System.Diagnostics;
using System.Text.Json;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Runs the portable PR reconciliation fixtures in CORE without contacting GitHub or a gateway.
/// Missing PowerShell, failed startup, timeout, malformed output and any fixture failure fail the gate.
/// </summary>
public sealed class BoundedPrReconciliationTests : ArchitectureTest
{
    /// <summary>Executes policy and host-template contracts with fake services and owned fixture storage.</summary>
    [Fact]
    public async Task ReconciliationFixtures_AllContractsPassInIsolatedSubprocess()
    {
        using var startup = new DocsLintScriptTests.PowerShellStartupState();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
            {
                WorkingDirectory = Repository.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-File",
            Repository.Path("scripts", "pr-execution", "tests", "Test-BoundedReconciliation.ps1"),
            "-ScratchRoot", Path.Combine(startup.Root, "fixtures"),
        })
            process.StartInfo.ArgumentList.Add(argument);
        startup.Configure(process.StartInfo);
        using var drainCancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        process.Start().ShouldBeTrue("fixture subprocess must start, never skip");
        var stdout = process.StandardOutput.ReadToEndAsync(drainCancellation.Token);
        var stderr = process.StandardError.ReadToEndAsync(drainCancellation.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).WaitAsync(deadline.Token);
            process.ExitCode.ShouldBe(0, await stdout + await stderr);
            using var result = JsonDocument.Parse(await stdout);
            result.RootElement.GetProperty("total").GetInt32().ShouldBeGreaterThanOrEqualTo(32);
            result.RootElement.GetProperty("passed").GetInt32()
                .ShouldBe(result.RootElement.GetProperty("total").GetInt32());
            result.RootElement.GetProperty("failed").GetInt32().ShouldBe(0);
            result.RootElement.GetProperty("failures").GetArrayLength().ShouldBe(0);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cleanup.Token)).WaitAsync(cleanup.Token);
            }
            finally
            {
                drainCancellation.Cancel();
            }
        }
    }
}
