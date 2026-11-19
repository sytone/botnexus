namespace BotNexus.Architecture.Tests;

/// <summary>
/// Preserves the application's timeout default and its explicit budget input to an externally
/// maintained runner. Template and runtime deadline contracts belong to botnexus-runner;
/// the application must not keep image source merely to compare numeric defaults.
/// </summary>
public class RunnerDeadlineConsistencyTests : ArchitectureTest
{

    private string Read(params string[] parts) => File.ReadAllText(Path.Combine(Repository.Root, Path.Combine(parts)));

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

    // Runtime original-clock/result and Docker COPY assertions moved to the standalone
    // Test-RunnerDeadlineContract and timeout/diagnostic fixtures with the runtime.
    [Fact]
    public void ApplicationReplicaTimeoutDefault_RemainsTwentyMinutes()
    {
        var client = Read("scripts", "repo", "Invoke-AzureBuildTest.ps1");
        Assert.Contains("[int]$ReplicaTimeoutMinutes = 20", client, StringComparison.Ordinal);
    }
}
