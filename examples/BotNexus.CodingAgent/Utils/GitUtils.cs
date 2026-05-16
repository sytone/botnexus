using System.Diagnostics;
using System.Text;

namespace BotNexus.CodingAgent.Utils;

public static class GitUtils
{
    public static async Task<string?> GetBranchAsync(string workingDir)
    {
        if (!await IsGitRepoAsync(workingDir).ConfigureAwait(false))
        {
            return null;
        }

        var result = await RunGitAsync(workingDir, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false);
        return result.ExitCode == 0 ? NormalizeOutput(result.Output) : null;
    }

    public static async Task<string?> GetStatusAsync(string workingDir)
    {
        if (!await IsGitRepoAsync(workingDir).ConfigureAwait(false))
        {
            return null;
        }

        var result = await RunGitAsync(workingDir, "status --short --branch").ConfigureAwait(false);
        return result.ExitCode == 0 ? NormalizeOutput(result.Output) : null;
    }

    public static async Task<bool> IsGitRepoAsync(string workingDir)
    {
        var result = await RunGitAsync(workingDir, "rev-parse --is-inside-work-tree").ConfigureAwait(false);
        return result.ExitCode == 0
            && string.Equals(NormalizeOutput(result.Output), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOutput(string value)
    {
        return value.ReplaceLineEndings(Environment.NewLine).Trim();
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(string workingDir, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDir}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return (-1, string.Empty);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        var output = new StringBuilder()
            .Append(stdout)
            .Append(stderr)
            .ToString();

        return (process.ExitCode, output);
    }
}
