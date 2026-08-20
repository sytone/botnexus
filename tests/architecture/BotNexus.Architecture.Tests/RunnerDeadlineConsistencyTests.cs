namespace BotNexus.Architecture.Tests;

/// <summary>
/// The remote gate's timeout budget is spelled in three places: <c>replicaTimeout</c> in the Bicep
/// template, the <c>ReplicaTimeoutMinutes</c> parameter on the client script, and the deadline the
/// container runner imposes on itself. They have to agree, because the whole mechanism of #3305
/// depends on the runner's deadline landing strictly INSIDE the platform's.
///
/// This is not hypothetical. Before #3305 the runner had no deadline at all: the platform killed
/// the replica at 20 minutes, the entrypoint's <c>finally</c> block never executed, and the run
/// uploaded an EMPTY artifact directory. Two measured <c>full</c> runs died exactly that way, so
/// the gate's only outcome was "killed, no evidence" -- unable to distinguish a hang from a slow
/// suite, and unable to name the project that was still executing.
///
/// A comment asking future editors to keep the numbers aligned is a wish; these tests fail the
/// build instead.
/// </summary>
public class RunnerDeadlineConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

    [Fact]
    public void ClientPassesTheReplicaBudgetToTheRunner()
    {
        var client = Read("scripts", "repo", "Invoke-AzureBuildTest.ps1");

        // The runner cannot derive a deadline it was never told about. If this env var stops
        // being sent, Get-RunnerDeadlineSeconds silently falls back to its default and the two
        // numbers are free to drift -- which is the drift class that produced the original
        // three-way disagreement over the runner image tag (#2900).
        Assert.Contains("REPLICA_TIMEOUT_SECONDS", client, StringComparison.Ordinal);
        Assert.Contains("$ReplicaTimeoutMinutes * 60", client, StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerBoundsTheTestPhaseAndRecordsTheTimeoutInTheContract()
    {
        var entrypoint = Read("infra", "buildtest", "runner", "entrypoint.ps1");

        // The bound must be applied to the test phase. A blocking `& dotnet test | Tee-Object`
        // pipeline cannot be interrupted, so reverting to one would restore the empty-artifact
        // outcome while every other part of this change still looked present.
        Assert.Contains("Invoke-BoundedProcess", entrypoint, StringComparison.Ordinal);
        Assert.Contains("Get-RunnerDeadlineSeconds", entrypoint, StringComparison.Ordinal);

        // The timeout has to be REPRESENTED in result.json, which is the contract the client
        // reads. A timeout that only reaches the console leaves the caller inferring an outcome
        // from an absence, which is the same unobservable-bound shape as #3244.
        Assert.Contains("timeout = $timeoutRecord", entrypoint, StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerHelperIsShippedIntoTheImage()
    {
        var dockerfile = Read("infra", "buildtest", "runner", "Dockerfile");

        // Dot-sourcing a file that was never COPYed fails the entrypoint at line one, which
        // would take out every mode of the gate rather than just the timeout path.
        Assert.Contains("RunnerTimeout.ps1", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployedReplicaTimeoutMatchesTheClientDefault()
    {
        var bicep = Read("infra", "buildtest", "main.bicep");
        var client = Read("scripts", "repo", "Invoke-AzureBuildTest.ps1");

        Assert.Contains("replicaTimeout: 1200", bicep, StringComparison.Ordinal);
        Assert.Contains("[int]$ReplicaTimeoutMinutes = 20", client, StringComparison.Ordinal);
    }
}
