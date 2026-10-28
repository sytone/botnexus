namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Fetches plugin content by cloning a git source into the caller's staging directory.
/// </summary>
/// <remarks>
/// Git is the settled transport for marketplace sources (#2623) because it supplies private-repo
/// authentication and version pinning without inventing either. The resolved version reported
/// back is the commit SHA, never the requested reference: recording "main" would make the
/// question "has the source moved since install?" unanswerable, which is exactly what update
/// needs to answer.
/// </remarks>
public sealed class GitPluginSourceFetcher : IPluginSourceFetcher
{
    private readonly IGitCommandRunner _git;

    /// <summary>Creates a fetcher over a git command runner.</summary>
    /// <param name="git">Command runner; substitutable so argument construction is testable.</param>
    public GitPluginSourceFetcher(IGitCommandRunner git)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    /// <inheritdoc />
    public async Task<PluginFetchResult> FetchAsync(
        string source,
        string? reference,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!GitPluginSourcePolicy.IsSupported(source))
        {
            throw new InvalidOperationException(GitPluginSourcePolicy.RejectedSourceMessage);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);

        // "--" terminates option parsing so a source beginning with a dash can never be
        // reinterpreted as a git option.
        List<string> cloneArgs = ["clone", "--quiet"];
        if (!string.IsNullOrWhiteSpace(reference))
        {
            cloneArgs.Add("--branch");
            cloneArgs.Add(reference);
        }

        cloneArgs.Add("--");
        cloneArgs.Add(source);
        cloneArgs.Add(".");

        try
        {
            var clone = await _git.RunAsync(stagingDirectory, cloneArgs, cancellationToken).ConfigureAwait(false);
            if (clone.ExitCode != 0)
            {
                throw new InvalidOperationException(GitPluginSourcePolicy.FetchFailureMessage);
            }

            var rev = await _git.RunAsync(stagingDirectory, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
            var objectId = rev.StandardOutput.Trim();
            if (rev.ExitCode != 0 || objectId.Length is not (40 or 64)
                || !objectId.All(char.IsAsciiHexDigit))
            {
                throw new InvalidOperationException(GitPluginSourcePolicy.FetchFailureMessage);
            }

            return new PluginFetchResult(objectId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Even cancellation exceptions can contain runner-supplied secrets or inner chains.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // Do not attach the original exception: ToString/loggers traverse inner exceptions.
            throw new InvalidOperationException(GitPluginSourcePolicy.FetchFailureMessage);
        }
    }
}
