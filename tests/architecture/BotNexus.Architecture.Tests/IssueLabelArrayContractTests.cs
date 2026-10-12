using System.Diagnostics;
using System.Text.Json;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Exercises the real issue-state helper through isolated CLI fixtures in remote CORE.
/// No GitHub request, credentials, gateway or shared platform state is used.
/// </summary>
public sealed class IssueLabelArrayContractTests : ArchitectureTest
{
    [Fact]
    public async Task IssueState_ReleasePreservesArrayShapeAndFailsClosed()
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
        start.ArgumentList.Add(Repository.Path("skills", "botnexus-pr-execution", "scripts", "Update-BotNexusIssue.Tests.ps1"));
        using var process = Process.Start(start);
        process.ShouldNotBeNull("PowerShell is required for the issue-state executable fixtures.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        var output = await stdout;
        var errors = await stderr;
        process.ExitCode.ShouldBe(0, output + errors);
        using var result = JsonDocument.Parse(output);
        result.RootElement.GetProperty("total").GetInt32().ShouldBe(12);
        result.RootElement.GetProperty("passed").GetInt32().ShouldBe(12);
        result.RootElement.GetProperty("failed").GetInt32().ShouldBe(0);
    }
}
