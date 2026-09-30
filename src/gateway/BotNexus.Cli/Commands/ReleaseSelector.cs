using System.Diagnostics;
using BotNexus.Gateway.Contracts.Updates;

namespace BotNexus.Cli.Commands;

internal static class ReleaseSelector
{
    internal static ReleaseTargetRequest ToRequest(bool latest, string? version)
    {
        if (latest && !string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("--latest and --version cannot be combined.");

        return latest
            ? ReleaseTargetRequest.Latest
            : string.IsNullOrWhiteSpace(version)
                ? ReleaseTargetRequest.Stable
                : ReleaseTargetRequest.Exact(version);
    }
}

internal static class ReleaseTargetGitResolver
{
    private const int CancelledExitCode = 130;

    internal static async Task<ResolvedReleaseTarget> ResolveRemoteAsync(
        string repository,
        ReleaseTargetRequest request,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            workingDirectory: null,
            ["ls-remote", repository, "refs/tags/*", "refs/heads/main"],
            cancellationToken);
        EnsureSucceeded(result, "Could not read release targets from the repository.");
        return Resolve(request, result.Output);
    }

    internal static async Task<ReleaseUpdateStatus> ResolveLocalAsync(
        string repoRoot,
        ReleaseTargetRequest request,
        CancellationToken cancellationToken)
    {
        var fetch = await RunGitAsync(repoRoot, ["fetch", "--tags", "origin", "main"], cancellationToken);
        EnsureSucceeded(fetch, "Could not fetch release targets from origin.");

        var refs = await RunGitAsync(
            repoRoot,
            ["for-each-ref", "--format=%(refname:short)%00%(*objectname)%00%(objectname)", "refs/tags"],
            cancellationToken);
        EnsureSucceeded(refs, "Could not enumerate release tags.");

        var tip = await RunGitAsync(repoRoot, ["rev-parse", "origin/main"], cancellationToken);
        EnsureSucceeded(tip, "Could not resolve the configured development tip origin/main.");

        var installed = await RunGitAsync(repoRoot, ["rev-parse", "HEAD"], cancellationToken);
        EnsureSucceeded(installed, "Could not resolve the installed source revision.");

        var metadata = refs.Output + Environment.NewLine + $"refs/heads/main\0\0{tip.Output.Trim()}";
        var target = Resolve(request, metadata, localFormat: true);
        return new ReleaseUpdateStatus(new ReleaseIdentity(null, installed.Output.Trim()), target);
    }

    internal static async Task CheckoutAsync(
        string repoRoot,
        ResolvedReleaseTarget target,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repoRoot, ["checkout", "--detach", target.CommitSha], cancellationToken);
        EnsureSucceeded(result, $"Could not check out release target '{target.SourceName}'.");
    }

    private static ResolvedReleaseTarget Resolve(
        ReleaseTargetRequest request,
        string output,
        bool localFormat = false)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        string? mainSha = null;

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (localFormat)
            {
                var fields = rawLine.Split('\0');
                if (fields.Length == 3)
                {
                    var referenceName = fields[0];
                    var sha = string.IsNullOrWhiteSpace(fields[1]) ? fields[2] : fields[1];
                    if (string.Equals(referenceName, "refs/heads/main", StringComparison.Ordinal))
                        mainSha = sha;
                    else if (!string.IsNullOrWhiteSpace(referenceName) && !string.IsNullOrWhiteSpace(sha))
                        tags[referenceName] = sha;
                    continue;
                }
            }

            var parts = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            if (parts[1] == "refs/heads/main")
                mainSha = parts[0];
            else if (parts[1].StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                var tag = parts[1]["refs/tags/".Length..];
                var peeled = tag.EndsWith("^{}", StringComparison.Ordinal);
                if (peeled)
                    tag = tag[..^3];
                if (peeled || !tags.ContainsKey(tag))
                    tags[tag] = parts[0];
            }
        }

        var developmentTip = string.IsNullOrWhiteSpace(mainSha)
            ? null
            : new DevelopmentTipReference("origin/main", mainSha);
        return ReleaseTargetResolver.Resolve(
            request,
            tags.Select(pair => new ReleaseTagReference(pair.Key, pair.Value)),
            developmentTip);
    }

    private static void EnsureSucceeded(GitResult result, string message)
    {
        if (result.Cancelled)
            throw new OperationCanceledException();
        if (result.ExitCode != 0)
            throw new ReleaseTargetResolutionException(
                string.IsNullOrWhiteSpace(result.Error) ? message : $"{message} {result.Error.Trim()}");
    }

    private static async Task<GitResult> RunGitAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new GitResult(1, string.Empty, "Failed to start git.", false);

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new GitResult(process.ExitCode, await outputTask, await errorTask, false);
        }
        catch (OperationCanceledException)
        {
            return new GitResult(CancelledExitCode, string.Empty, string.Empty, true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitResult(1, string.Empty, ex.Message, false);
        }
    }

    private readonly record struct GitResult(int ExitCode, string Output, string Error, bool Cancelled);
}
