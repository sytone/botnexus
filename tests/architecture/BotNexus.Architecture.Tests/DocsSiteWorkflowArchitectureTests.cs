using System.Diagnostics;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Runs the same standalone Git fixtures and workflow contract in the remote core suite.
/// No gateway or network is needed; the standalone script is also usable for focused TDD.
/// </summary>
public sealed class DocsSiteWorkflowArchitectureTests : ArchitectureTest
{
    [Fact]
    public async Task DocsSite_ClassifierAndWorkflowContract_PassExecutableFixtures()
    {
        var start = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = Repository.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Repository.Path("tests", "architecture", "BotNexus.Architecture.Tests", "DocsSiteFocusedTests.ps1"));
        using var process = Process.Start(start);
        process.ShouldNotBeNull("PowerShell must be available to run docs-site contract tests.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        var output = await stdout + await stderr;
        process.ExitCode.ShouldBe(0, output);
        output.ShouldContain("RESULT passed=31 failed=0");
    }
}
