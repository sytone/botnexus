using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration;

public sealed class ExtensionProjectBuildService(
    IExtensionProjectBuildRunner runner,
    Func<string> userProfileProvider,
    Action<string> report,
    IFileSystem? fileSystem = null)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? new FileSystem();

    public async Task<ExtensionProjectBuildResult> BuildAfterMainAsync(
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

    public async Task<ExtensionProjectBuildResult> BuildAsync(
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

    public static string ResolveBotNexusRepoRoot(string? explicitRoot, string userProfile)
        => Path.GetFullPath(explicitRoot ?? Path.Combine(userProfile, "botnexus"));
}

public sealed record ExtensionProjectBuildInvocation(
    string ProjectPath,
    string BotNexusRepoRoot,
    string StagingDirectory);

public sealed record ExtensionProjectBuildResult(
    int ExitCode,
    string BotNexusRepoRoot,
    string StagingDirectory);

public interface IExtensionProjectBuildRunner
{
    Task<int> RunAsync(ExtensionProjectBuildInvocation invocation, CancellationToken cancellationToken);
}

public sealed class DotNetExtensionProjectBuildRunner(bool verbose) : IExtensionProjectBuildRunner
{
    public async Task<int> RunAsync(ExtensionProjectBuildInvocation invocation, CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = invocation.BotNexusRepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "build", invocation.ProjectPath, "-c", "Release", "--nologo", "--tl:off",
            $"/p:BotNexusRepoRoot={invocation.BotNexusRepoRoot}",
            $"/p:OutputPath={invocation.StagingDirectory}{Path.DirectorySeparatorChar}",
            "/p:AppendTargetFrameworkToOutputPath=false",
            "/p:AppendRuntimeIdentifierToOutputPath=false"
        }) startInfo.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start extension build process.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var text = (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false));
        if (verbose || process.ExitCode != 0) Console.Write(text);
        return process.ExitCode;
    }
}
