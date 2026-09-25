using System.Collections.Concurrent;
using System.Diagnostics;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands;

internal sealed record ExtensionRepositoryReconciliationResult(string Id, bool Succeeded, string? ResolvedCommit, string? FailureName, string? Diagnostic);

/// <summary>Materializes registered sources without reset, clean, force, or deletion.</summary>
internal sealed class ExtensionRepositoryCloneReconciler
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CloneLocks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _home;
    private readonly ExtensionRepositoryRegistryService _registry;
    private readonly TimeProvider _timeProvider;

    internal ExtensionRepositoryCloneReconciler(string home, ExtensionRepositoryRegistryService registry, TimeProvider? timeProvider = null)
    {
        _home = Path.GetFullPath(home);
        _registry = registry;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<IReadOnlyList<ExtensionRepositoryReconciliationResult>> ReconcileEnabledAsync(CancellationToken cancellationToken = default)
    {
        var registrations = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<ExtensionRepositoryReconciliationResult>();
        foreach (var registration in registrations.Where(item => item.Enabled))
            results.Add(await ReconcileAsync(registration, cancellationToken).ConfigureAwait(false));
        return results;
    }

    internal async Task<ExtensionRepositoryReconciliationResult> ReconcileAsync(ExtensionRepositoryRegistrationInfo registration, CancellationToken cancellationToken = default)
    {
        var clonePath = Path.GetFullPath(Path.Combine(_home, "extension-repositories", registration.Id, "repository"));
        var cloneLock = CloneLocks.GetOrAdd(clonePath, _ => new SemaphoreSlim(1, 1));
        await cloneLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _registry.RecordReconciliationAttemptAsync(registration.Id, clonePath, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            var outcome = await ReconcileLockedAsync(registration, clonePath, cancellationToken).ConfigureAwait(false);
            if (outcome.Succeeded)
                await _registry.RecordReconciliationSuccessAsync(registration.Id, outcome.ResolvedCommit!, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            else
                await _registry.RecordReconciliationFailureAsync(registration.Id, outcome.FailureName!, outcome.Diagnostic!, cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            const string failure = "git-operation-failed";
            await _registry.RecordReconciliationFailureAsync(registration.Id, failure, ex.Message, cancellationToken).ConfigureAwait(false);
            return Failed(registration.Id, failure, ex.Message);
        }
        finally { cloneLock.Release(); }
    }

    private static async Task<ExtensionRepositoryReconciliationResult> ReconcileLockedAsync(ExtensionRepositoryRegistrationInfo registration, string clonePath, CancellationToken cancellationToken)
    {
        var newlyCloned = !Directory.Exists(clonePath);
        if (newlyCloned)
        {
            var parent = Path.GetDirectoryName(clonePath) ?? throw new InvalidOperationException($"Clone path '{clonePath}' has no parent directory.");
            Directory.CreateDirectory(parent);
            var clone = await RunGitAsync(parent, ["clone", "--no-checkout", "--origin", "origin", "--", registration.RepositoryUrl, clonePath], cancellationToken).ConfigureAwait(false);
            if (!clone.Succeeded) return Failed(registration.Id, "clone-failed", clone.Diagnostic);
        }
        else
        {
            var workTree = await RunGitAsync(clonePath, ["rev-parse", "--is-inside-work-tree"], cancellationToken).ConfigureAwait(false);
            if (!workTree.Succeeded || !string.Equals(workTree.Output, "true", StringComparison.OrdinalIgnoreCase)) return Failed(registration.Id, "missing-origin", "The managed path is not a Git working tree.");
            var status = await RunGitAsync(clonePath, ["status", "--porcelain", "--untracked-files=normal"], cancellationToken).ConfigureAwait(false);
            if (!status.Succeeded) return Failed(registration.Id, "git-operation-failed", status.Diagnostic);
            if (!string.IsNullOrWhiteSpace(status.Output)) return Failed(registration.Id, "dirty", "The managed clone contains tracked or untracked changes.");
            var origin = await RunGitAsync(clonePath, ["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false);
            if (!origin.Succeeded || string.IsNullOrWhiteSpace(origin.Output)) return Failed(registration.Id, "missing-origin", "The managed clone has no origin remote.");
            if (!string.Equals(origin.Output, registration.RepositoryUrl, StringComparison.Ordinal)) return Failed(registration.Id, "missing-origin", "The origin URL does not match the registration.");
            if (registration.UpdatesEnabled)
            {
                var fetch = await RunGitAsync(clonePath, ["fetch", "--prune", "origin"], cancellationToken).ConfigureAwait(false);
                if (!fetch.Succeeded) return Failed(registration.Id, "fetch-failed", fetch.Diagnostic);
            }
        }

        var resolved = await ResolveCommitAsync(clonePath, registration.RequestedRef, cancellationToken).ConfigureAwait(false);
        if (resolved is null) return Failed(registration.Id, "invalid-ref", $"Ref '{registration.RequestedRef}' does not resolve to a commit.");
        var head = await RunGitAsync(clonePath, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!newlyCloned && head.Succeeded && !string.Equals(head.Output, resolved, StringComparison.OrdinalIgnoreCase))
        {
            var ancestor = await RunGitAsync(clonePath, ["merge-base", "--is-ancestor", head.Output, resolved], cancellationToken).ConfigureAwait(false);
            if (!ancestor.Succeeded) return Failed(registration.Id, "diverged", $"Current commit {head.Output} is not an ancestor of {resolved}.");
        }
        var checkout = await RunGitAsync(clonePath, ["checkout", "--detach", resolved], cancellationToken).ConfigureAwait(false);
        return checkout.Succeeded ? new(registration.Id, true, resolved, null, null) : Failed(registration.Id, "git-operation-failed", checkout.Diagnostic);
    }

    private static async Task<string?> ResolveCommitAsync(string clonePath, string requestedRef, CancellationToken cancellationToken)
    {
        string[] candidates = [$"refs/remotes/origin/{requestedRef}^{{commit}}", $"refs/tags/{requestedRef}^{{commit}}", $"{requestedRef}^{{commit}}"];
        foreach (var candidate in candidates)
        {
            var result = await RunGitAsync(clonePath, ["rev-parse", "--verify", candidate], cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.Output.Length == 40 && result.Output.All(Uri.IsHexDigit)) return result.Output.ToLowerInvariant();
        }
        return null;
    }

    private static ExtensionRepositoryReconciliationResult Failed(string id, string name, string diagnostic) => new(id, false, null, name, diagnostic);

    private static async Task<GitResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await outputTask.ConfigureAwait(false)).Trim();
        var error = (await errorTask.ConfigureAwait(false)).Trim();
        return new(process.ExitCode, output, string.IsNullOrWhiteSpace(error) ? output : error);
    }

    private sealed record GitResult(int ExitCode, string Output, string Diagnostic) { internal bool Succeeded => ExitCode == 0; }
}
