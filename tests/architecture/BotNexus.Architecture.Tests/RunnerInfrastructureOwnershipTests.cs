namespace BotNexus.Architecture.Tests;

/// <summary>Prevents the application from regaining ownership of runner infrastructure or image runtime.</summary>
public sealed class RunnerInfrastructureOwnershipTests : ArchitectureTest
{
    [Theory]
    [InlineData("Deploy-BuildTestInfrastructure.ps1")]
    [InlineData("main.bicep")]
    [InlineData("network.bicep")]
    [InlineData("runner/Dockerfile")]
    [InlineData("runner/entrypoint.ps1")]
    [InlineData("runner/RunnerBuild.ps1")]
    [InlineData("runner/RunnerCost.ps1")]
    [InlineData("runner/DiagnosticContainment.cs")]
    [InlineData("runner/DiagnosticOwnedCommand.ps1")]
    public void InfrastructureAndRuntime_AreNotApplicationSource(string relativePath)
    {
        File.Exists(Path.Combine(Repository.Root, "infra", "buildtest", relativePath)).ShouldBeFalse(
            "Runner infrastructure and runtime belong to sytone/botnexus-runner.");
    }

    [Theory]
    [InlineData("Assert-TestResultContract.ps1")]
    [InlineData("Invoke-BoundedCoreTests.ps1")]
    [InlineData("Invoke-AzureBuildTest.ps1")]
    public void ApplicationValidation_DoesNotImportTheRemovedRuntime(string script)
    {
        var source = File.ReadAllText(Repository.Path("scripts", "repo", script));
        source.ShouldNotContain("infra/buildtest/runner");
        source.ShouldNotContain("infra/buildtest/RunnerImageProvenance.ps1");
    }
}
