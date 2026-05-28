namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Test 2: verify the installed CLI emits sensible help output that advertises the core commands.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class CliHelpTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly CliInstallFixture _fixture;

    public CliHelpTests(CliInstallFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Cli_Help_ListsCoreCommands()
    {
        _fixture.InstallSucceeded.ShouldBeTrue(
            "CLI install fixture did not complete successfully — see CliInstallationTests for the install failure.");

        var result = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            "--help",
            timeout: CommandTimeout);

        result.ExitCode.ShouldBe(
            0,
            $"botnexus --help exited with {result.ExitCode}.\nStdOut:\n{result.StdOut}\nStdErr:\n{result.StdErr}");

        var output = result.Combined;
        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("BotNexus");

        // Sanity-check that the published CLI exposes the commands this test project relies on.
        foreach (var expected in new[] { "init", "install", "validate", "agent" })
        {
            output.ShouldContain(
                expected,
                Case.Insensitive,
                customMessage: $"Help output did not advertise expected command '{expected}'. Full output:\n{output}");
        }
    }
}
