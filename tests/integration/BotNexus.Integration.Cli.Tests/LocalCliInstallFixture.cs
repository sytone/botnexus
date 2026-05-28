using System.Runtime.InteropServices;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// xUnit collection fixture that packs the in-tree BotNexus.Cli source as a NuGet
/// global tool with a unique pre-release version and installs it into an isolated
/// --tool-path. Lets integration tests exercise pre-release CLI features (e.g. the
/// integration-mock provider, non-interactive `provider add`) before they ship to
/// nuget.org.
///
/// Contrast with <see cref="CliInstallFixture"/>, which validates the
/// already-published package on nuget.org. This fixture is the harness for
/// PR-time validation of CLI changes.
///
/// The pack-and-install runs once per test run. Failures are captured (not thrown)
/// so dependent tests can skip gracefully and the install diagnostics are visible
/// in test output.
/// </summary>
public sealed class LocalCliInstallFixture : IAsyncLifetime
{
    private const string PackageId = "BotNexus.Cli";
    private static readonly TimeSpan PackTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);

    public string PackVersion { get; private set; } = string.Empty;
    public string PackOutputDir { get; private set; } = string.Empty;
    public string ToolPath { get; private set; } = string.Empty;
    public string CliExecutablePath { get; private set; } = string.Empty;

    public bool Succeeded { get; private set; }
    public int PackExitCode { get; private set; } = -1;
    public int InstallExitCode { get; private set; } = -1;
    public string PackOutput { get; private set; } = string.Empty;
    public string InstallOutput { get; private set; } = string.Empty;
    public string? Error { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            var runId = Guid.NewGuid().ToString("N");
            PackVersion = $"99.99.99-local-{runId[..8]}";
            var sandboxRoot = Path.Combine(Path.GetTempPath(), "botnexus-local-cli", runId);
            PackOutputDir = Path.Combine(sandboxRoot, "pack");
            ToolPath = Path.Combine(sandboxRoot, "tool");
            Directory.CreateDirectory(PackOutputDir);
            Directory.CreateDirectory(ToolPath);

            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "botnexus.exe" : "botnexus";
            CliExecutablePath = Path.Combine(ToolPath, exeName);

            var repoRoot = RepoLocator.FindRepoRoot();
            var cliProject = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Cli", "BotNexus.Cli.csproj");

            // --- pack -------------------------------------------------------
            var packResult = await ProcessRunner.RunAsync(
                "dotnet",
                $"pack \"{cliProject}\" --configuration Release --output \"{PackOutputDir}\" /p:Version={PackVersion} /p:PackageVersion={PackVersion} --nologo",
                timeout: PackTimeout);

            PackExitCode = packResult.ExitCode;
            PackOutput = packResult.Combined;
            if (PackExitCode != 0)
                return;

            // --- install ----------------------------------------------------
            var installResult = await ProcessRunner.RunAsync(
                "dotnet",
                $"tool install --tool-path \"{ToolPath}\" --add-source \"{PackOutputDir}\" --version {PackVersion} {PackageId}",
                timeout: InstallTimeout);

            InstallExitCode = installResult.ExitCode;
            InstallOutput = installResult.Combined;
            Succeeded = installResult.ExitCode == 0 && File.Exists(CliExecutablePath);
        }
        catch (Exception ex)
        {
            Error = ex.ToString();
            Succeeded = false;
        }
    }

    public Task DisposeAsync()
    {
        // Walk back to the per-run sandbox root and remove the whole tree.
        try
        {
            var sandboxRoot = Path.GetDirectoryName(ToolPath);
            if (!string.IsNullOrEmpty(sandboxRoot) && Directory.Exists(sandboxRoot))
                Directory.Delete(sandboxRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; locked .nupkg or tool files on Windows are not worth failing the suite over.
        }
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class LocalCliCollection : ICollectionFixture<LocalCliInstallFixture>
{
    public const string Name = "Local CLI install collection";
}
