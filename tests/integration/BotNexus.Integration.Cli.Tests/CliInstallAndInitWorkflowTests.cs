namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Test 3: drive the installed CLI through the <c>install</c> and <c>init</c> flow.
///
/// Uses a fresh temp directory as a self-contained sandbox:
///   - <c>&lt;tmp&gt;/source</c>     → target for <c>botnexus install --source</c> (git clones the current repo here)
///   - <c>&lt;tmp&gt;/.botnexus</c>  → target for <c>botnexus init --target</c>     (config home)
///
/// The <c>--repo</c> argument points at the current repository on disk so the test
/// does not require external network for the git clone step (the CLI itself was
/// installed from nuget.org by the fixture).
/// </summary>
[Collection(CliCollection.Name)]
public sealed class CliInstallAndInitWorkflowTests : IAsyncLifetime
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly CliInstallFixture _fixture;
    private string _sandbox = string.Empty;

    public CliInstallAndInitWorkflowTests(CliInstallFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "botnexus-cli-flow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_sandbox))
                Directory.Delete(_sandbox, recursive: true);
        }
        catch
        {
            // Cloned .git directories can have read-only files on Windows; best-effort cleanup.
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Cli_Install_ClonesCurrentRepoIntoSandbox()
    {
        _fixture.InstallSucceeded.ShouldBeTrue(
            "CLI install fixture did not complete successfully — see CliInstallationTests for the install failure.");

        var repoRoot = RepoLocator.FindRepoRoot();
        var sourceDir = Path.Combine(_sandbox, "source");

        var result = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"install --source \"{sourceDir}\" --repo \"{repoRoot}\"",
            timeout: CommandTimeout);

        result.ExitCode.ShouldBe(
            0,
            $"botnexus install failed.\nStdOut:\n{result.StdOut}\nStdErr:\n{result.StdErr}");

        Directory.Exists(sourceDir).ShouldBeTrue(
            $"Expected source directory at {sourceDir} after install.");
        Directory.Exists(Path.Combine(sourceDir, ".git")).ShouldBeTrue(
            "Cloned source should contain a .git directory.");
        File.Exists(Path.Combine(sourceDir, "BotNexus.slnx")).ShouldBeTrue(
            "Cloned source should contain BotNexus.slnx (proves the clone is of the current repo).");
    }

    [Fact]
    public async Task Cli_Init_CreatesConfigInIsolatedHome()
    {
        _fixture.InstallSucceeded.ShouldBeTrue(
            "CLI install fixture did not complete successfully — see CliInstallationTests for the install failure.");

        var configHome = Path.Combine(_sandbox, ".botnexus");

        // Explicit --target is the contract this test pins; BOTNEXUS_HOME is also cleared
        // to make sure nothing leaks from the test host into the CLI invocation.
        var env = new Dictionary<string, string?>
        {
            ["BOTNEXUS_HOME"] = null
        };

        var result = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"init --target \"{configHome}\"",
            environment: env,
            timeout: CommandTimeout);

        result.ExitCode.ShouldBe(
            0,
            $"botnexus init failed.\nStdOut:\n{result.StdOut}\nStdErr:\n{result.StdErr}");

        Directory.Exists(configHome).ShouldBeTrue(
            $"Expected init to create config home at {configHome}.");
        File.Exists(Path.Combine(configHome, "config.json")).ShouldBeTrue(
            "Expected init to create config.json in the target home.");
    }
}
