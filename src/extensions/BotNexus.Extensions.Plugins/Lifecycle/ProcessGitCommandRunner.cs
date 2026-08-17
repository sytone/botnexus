using System.Diagnostics;
using System.Text;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Runs <c>git</c> as a child process with arguments passed individually, so no shell is
/// involved and a source URL containing shell metacharacters cannot be interpreted.
/// </summary>
public sealed class ProcessGitCommandRunner : IGitCommandRunner
{
    private readonly string _gitExecutable;

    /// <summary>Creates a runner.</summary>
    /// <param name="gitExecutable">Git executable name or path; resolved on PATH by default.</param>
    public ProcessGitCommandRunner(string gitExecutable = "git")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutable);
        _gitExecutable = gitExecutable;
    }

    /// <inheritdoc />
    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Never let git block on an interactive credential or host-key prompt: a hung clone
        // inside a scheduled update would stall with no diagnostic at all.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new GitCommandResult(process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
