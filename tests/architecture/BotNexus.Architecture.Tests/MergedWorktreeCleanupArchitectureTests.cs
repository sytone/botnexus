using System.Diagnostics;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Executes merged-worktree selection and refusal fixtures in the remote CORE gate.
/// Fake Git, GitHub and removal boundaries require no gateway, credentials or deletion.
/// </summary>
public sealed class MergedWorktreeCleanupArchitectureTests : ArchitectureTest
{
    [Fact]
    public async Task MergedWorktreeCleanup_ExecutableSafetyFixtures_Pass()
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
        start.ArgumentList.Add(Repository.Path("skills", "botnexus-pr-execution", "scripts", "Test-MergedWorktreeCleanup.ps1"));
        using var process = Process.Start(start);
        process.ShouldNotBeNull("PowerShell must be available to run merged-worktree fixtures.");
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
        output.ShouldContain("RESULT passed=44 failed=0");
    }
}
