using System.IO.Abstractions;

namespace BotNexus.Cli.Commands;

internal sealed class ExtensionProjectBuildService(
    IExtensionProjectBuildRunner runner,
    Func<string> userProfileProvider,
    Action<string> report,
    IFileSystem? fileSystem = null)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();

    internal async Task<ExtensionProjectBuildResult> BuildAfterMainAsync(
        Func<CancellationToken, Task<int>> buildMainAsync,
        string projectPath,
        string stagingDirectory,
        string? botNexusRepoRoot,
        CancellationToken cancellationToken)
    {
        var mainExitCode = await buildMainAsync(cancellationToken);
        if (mainExitCode != 0)
            return new ExtensionProjectBuildResult(mainExitCode, string.Empty, string.Empty);

        return await BuildAsync(projectPath, stagingDirectory, botNexusRepoRoot, cancellationToken);
    }

    internal async Task<ExtensionProjectBuildResult> BuildAsync(
        string projectPath,
        string stagingDirectory,
        string? botNexusRepoRoot,
        CancellationToken cancellationToken)
    {
        var resolvedRoot = ResolveBotNexusRepoRoot(botNexusRepoRoot, userProfileProvider());
        var resolvedProject = _fileSystem.Path.GetFullPath(projectPath);
        var resolvedStaging = _fileSystem.Path.GetFullPath(stagingDirectory);

        if (!_fileSystem.Directory.Exists(resolvedRoot))
            throw new DirectoryNotFoundException($"BotNexus repository root does not exist: {resolvedRoot}");
        if (!_fileSystem.File.Exists(resolvedProject))
            throw new FileNotFoundException($"Extension project does not exist: {resolvedProject}", resolvedProject);

        report($"BotNexusRepoRoot: {resolvedRoot}");
        var invocation = new ExtensionProjectBuildInvocation(resolvedProject, resolvedRoot, resolvedStaging);
        var exitCode = await runner.RunAsync(invocation, cancellationToken);
        return new ExtensionProjectBuildResult(exitCode, resolvedRoot, resolvedStaging);
    }

    internal static string ResolveBotNexusRepoRoot(string? explicitRoot, string userProfile)
        => Path.GetFullPath(explicitRoot ?? Path.Combine(userProfile, "botnexus"));
}

internal sealed record ExtensionProjectBuildInvocation(
    string ProjectPath,
    string BotNexusRepoRoot,
    string StagingDirectory);

internal sealed record ExtensionProjectBuildResult(
    int ExitCode,
    string BotNexusRepoRoot,
    string StagingDirectory);

internal interface IExtensionProjectBuildRunner
{
    Task<int> RunAsync(ExtensionProjectBuildInvocation invocation, CancellationToken cancellationToken);
}

internal sealed class BuildOutputExtensionProjectRunner(bool verbose) : IExtensionProjectBuildRunner
{
    public Task<int> RunAsync(ExtensionProjectBuildInvocation invocation, CancellationToken cancellationToken)
        => BuildOutputStreamer.RunExtensionAsync(
            invocation.ProjectPath,
            invocation.BotNexusRepoRoot,
            invocation.StagingDirectory,
            verbose,
            cancellationToken);
}
