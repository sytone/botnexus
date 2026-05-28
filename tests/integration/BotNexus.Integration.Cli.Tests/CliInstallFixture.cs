using System.Runtime.InteropServices;

namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// xUnit collection fixture that installs the BotNexus.Cli .NET global tool from nuget.org
/// into an isolated --tool-path so tests can exercise the published binary without
/// polluting the host's global tool catalogue.
///
/// The install runs once per test run. Failures are captured (not thrown) so that
/// <see cref="CliInstallationTests"/> can assert against them explicitly while still
/// allowing dependent tests to skip gracefully.
/// </summary>
public sealed class CliInstallFixture : IAsyncLifetime
{
    private const string PackageId = "BotNexus.Cli";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);

    public string ToolPath { get; private set; } = string.Empty;
    public string CliExecutablePath { get; private set; } = string.Empty;
    public bool InstallSucceeded { get; private set; }
    public int InstallExitCode { get; private set; } = -1;
    public string InstallOutput { get; private set; } = string.Empty;
    public string? InstallError { get; private set; }

    public async Task InitializeAsync()
    {
        // Each test run gets a fresh tool-path so re-installs don't see "already installed" errors.
        ToolPath = Path.Combine(Path.GetTempPath(), "botnexus-integration-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ToolPath);

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "botnexus.exe" : "botnexus";
        CliExecutablePath = Path.Combine(ToolPath, exeName);

        try
        {
            var result = await ProcessRunner.RunAsync(
                "dotnet",
                $"tool install --tool-path \"{ToolPath}\" {PackageId}",
                timeout: InstallTimeout);

            InstallExitCode = result.ExitCode;
            InstallOutput = result.Combined;
            InstallSucceeded = result.ExitCode == 0 && File.Exists(CliExecutablePath);
        }
        catch (Exception ex)
        {
            InstallError = ex.ToString();
            InstallSucceeded = false;
        }
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(ToolPath))
                Directory.Delete(ToolPath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; locked files on Windows are not worth failing the suite over.
        }
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class CliCollection : ICollectionFixture<CliInstallFixture>
{
    public const string Name = "CLI install collection";
}
