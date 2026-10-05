namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Exercises current-checkout --latest behavior with the CLI packed from this checkout.
/// The separate NuGet-installed CLI collection remains responsible for published-package
/// install and init coverage; its version can legitimately lag source behavior.
/// </summary>
[Collection(LocalCliCollection.Name)]
public sealed class LocalCliLatestInstallTests : IAsyncLifetime
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly LocalCliInstallFixture _fixture;
    private string _sandbox = string.Empty;

    public LocalCliLatestInstallTests(LocalCliInstallFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "botnexus-local-cli-latest", Guid.NewGuid().ToString("N"));
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
            // The cloned .git directory can contain read-only files on Windows; best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Cli_Install_LatestClonesCurrentRepoWithoutReleaseTagsIntoSandbox()
    {
        _fixture.Succeeded.ShouldBeTrue(
            $"Local CLI pack/install did not complete. Error: {_fixture.Error}; " +
            $"pack exit: {_fixture.PackExitCode}\n{_fixture.PackOutput}\n" +
            $"install exit: {_fixture.InstallExitCode}\n{_fixture.InstallOutput}\n" +
            $"layout: {_fixture.LayoutFailure ?? _fixture.PackIsolationFailure}");

        var sourceDir = Path.Combine(_sandbox, "source");
        var repoRoot = RepoLocator.FindRepoRoot();

        // CI checks out a detached commit: neither release tags nor refs/heads/main are
        // guaranteed. The fixture-owned bare remote exposes this exact checkout as main,
        // which is the development tip selected by --latest.
        var localRepo = await TagFreeLocalSourceRepository.CreateAsync(_sandbox, repoRoot, CommandTimeout);
        var currentCommit = await ProcessRunner.RunAsync(
            "git", $"-C \"{repoRoot}\" rev-parse HEAD", timeout: CommandTimeout);
        currentCommit.ExitCode.ShouldBe(0, currentCommit.Combined);
        var expectedCommit = currentCommit.StdOut.Trim();
        expectedCommit.ShouldNotBeNullOrWhiteSpace("HEAD must not be empty.");

        var result = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"install --latest --source \"{sourceDir}\" --repo \"{localRepo}\"",
            timeout: CommandTimeout);

        result.ExitCode.ShouldBe(
            0,
            $"botnexus install failed.\nStdOut:\n{result.StdOut}\nStdErr:\n{result.StdErr}");

        Directory.Exists(sourceDir).ShouldBeTrue(
            $"Expected source directory at {sourceDir} after install.");
        Directory.Exists(Path.Combine(sourceDir, ".git")).ShouldBeTrue(
            "Cloned source should contain a .git directory.");
        File.Exists(Path.Combine(sourceDir, "dirs.proj")).ShouldBeTrue(
            "Cloned source should contain the root traversal project (proves the clone is of the current repo).");
        var installedCommit = await ProcessRunner.RunAsync(
            "git", $"-C \"{sourceDir}\" rev-parse HEAD", timeout: CommandTimeout);
        installedCommit.ExitCode.ShouldBe(0, installedCommit.Combined);
        installedCommit.StdOut.Trim().ShouldBe(expectedCommit,
            "The tag-free fixture must install the exact current checkout, not an ambient branch tip.");
    }
}
