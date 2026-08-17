namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>Result of running a single git command.</summary>
/// <param name="ExitCode">Process exit code; zero means success.</param>
/// <param name="StandardOutput">Captured stdout, trimmed.</param>
/// <param name="StandardError">Captured stderr, trimmed.</param>
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs a git command. Separated from <see cref="GitPluginSourceFetcher"/> so the fetcher's
/// argument construction and error handling can be exercised without spawning a process.
/// </summary>
public interface IGitCommandRunner
{
    /// <summary>Runs <c>git</c> with the supplied arguments in <paramref name="workingDirectory"/>.</summary>
    /// <param name="workingDirectory">Directory to run in; must exist.</param>
    /// <param name="arguments">Arguments passed individually so no shell quoting is involved.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}
