namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Test 1 (also the setup test for the project): verify that <c>BotNexus.Cli</c> is
/// available on nuget.org and can be installed as a .NET global tool. The actual
/// install is performed once by <see cref="CliInstallFixture"/>; this class asserts
/// the result so failures surface as a normal test failure rather than a fixture crash.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class CliInstallationTests
{
    private readonly CliInstallFixture _fixture;

    public CliInstallationTests(CliInstallFixture fixture) => _fixture = fixture;

    [Fact]
    public void CliPackage_IsInstallableFromNuget()
    {
        _fixture.InstallError.ShouldBeNull(
            $"dotnet tool install threw before exiting:\n{_fixture.InstallError}");

        _fixture.InstallExitCode.ShouldBe(
            0,
            $"dotnet tool install BotNexus.Cli failed.\nExit: {_fixture.InstallExitCode}\nOutput:\n{_fixture.InstallOutput}");

        File.Exists(_fixture.CliExecutablePath).ShouldBeTrue(
            $"Expected CLI executable at {_fixture.CliExecutablePath} after install. " +
            $"Install output:\n{_fixture.InstallOutput}");

        _fixture.InstallSucceeded.ShouldBeTrue();
    }
}
