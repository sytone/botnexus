using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Agents;

/// <summary>Captures and retains bounded recovery evidence from caller-granted Git worktrees.</summary>
public interface ISubAgentWorktreeSnapshotService
{
    /// <summary>Attempts one bounded snapshot without inspecting paths outside the supplied grants.</summary>
    Task<SubAgentWorktreeSnapshot> CaptureAsync(
        string subAgentId,
        IReadOnlyList<string> grantedWritePaths,
        CancellationToken ct);

    /// <summary>Deletes a previously captured artifact when its terminal record loses a race.</summary>
    Task DeleteArtifactAsync(string artifactPath, CancellationToken ct);

    /// <summary>Runs one bounded retention pass over the configured artifact directory.</summary>
    Task<int> SweepArtifactsAsync(CancellationToken ct);
}

internal sealed class SubAgentWorktreeSnapshotOptions
{
    public string ArtifactRoot { get; set; } = string.Empty;
    public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(3);
    public int MaxPatchBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxUntrackedPaths { get; set; } = 100;
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
    public int MaxRetainedArtifacts { get; set; } = 100;
    public int MaxSweepFiles { get; set; } = 1_000;
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
}

internal enum GitSnapshotProcessOutcome
{
    Succeeded,
    Failed,
    TimedOut,
    OutputLimitExceeded
}

internal sealed record GitSnapshotProcessCall(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    int MaxOutputBytes);

internal sealed record GitSnapshotProcessResult(
    GitSnapshotProcessOutcome Outcome,
    string StandardOutput,
    int? ExitCode)
{
    public static GitSnapshotProcessResult Succeed(string output) => new(GitSnapshotProcessOutcome.Succeeded, output, 0);
    public static GitSnapshotProcessResult Failed(int exitCode) => new(GitSnapshotProcessOutcome.Failed, string.Empty, exitCode);
    public static GitSnapshotProcessResult TimedOut() => new(GitSnapshotProcessOutcome.TimedOut, string.Empty, null);
    public static GitSnapshotProcessResult OutputLimit() => new(GitSnapshotProcessOutcome.OutputLimitExceeded, string.Empty, null);
}

internal interface IGitSnapshotProcessRunner
{
    Task<GitSnapshotProcessResult> RunAsync(GitSnapshotProcessCall call, CancellationToken ct);
}

internal sealed class GitSnapshotProcessRunner : IGitSnapshotProcessRunner
{
    public async Task<GitSnapshotProcessResult> RunAsync(GitSnapshotProcessCall call, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = call.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in call.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return GitSnapshotProcessResult.Failed(-1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return GitSnapshotProcessResult.Failed(-1);
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, call.MaxOutputBytes, ct);
        _ = stdoutTask.ContinueWith(
            _ => TryKill(process),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var stderrTask = ReadBoundedAsync(process.StandardError, 16 * 1024, ct);
        _ = stderrTask.ContinueWith(
            _ => TryKill(process),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            var waitTask = process.WaitForExitAsync(ct);
            await Task.WhenAll(waitTask, stdoutTask, stderrTask).ConfigureAwait(false);
            var output = await stdoutTask.ConfigureAwait(false);
            return process.ExitCode == 0
                ? GitSnapshotProcessResult.Succeed(output)
                : GitSnapshotProcessResult.Failed(process.ExitCode);
        }
        catch (OutputLimitExceededException)
        {
            TryKill(process);
            return GitSnapshotProcessResult.OutputLimit();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return GitSnapshotProcessResult.TimedOut();
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken ct)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var bytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                return builder.ToString();
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (bytes > maxBytes)
                throw new OutputLimitExceededException();
            builder.Append(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class OutputLimitExceededException : Exception;
}

internal sealed class SubAgentWorktreeSnapshotService(
    IFileSystem fileSystem,
    IOptions<SubAgentWorktreeSnapshotOptions> optionsAccessor,
    IGitSnapshotProcessRunner processRunner,
    ISecretRedactor secretRedactor,
    BotNexusHome botNexusHome,
    TimeProvider timeProvider,
    ILogger<SubAgentWorktreeSnapshotService> logger) : ISubAgentWorktreeSnapshotService
{
    private const int ProbeOutputLimit = 16 * 1024;
    private readonly SubAgentWorktreeSnapshotOptions _options = optionsAccessor.Value;
    private readonly SemaphoreSlim _artifactGate = new(1, 1);

    public async Task<SubAgentWorktreeSnapshot> CaptureAsync(
        string subAgentId,
        IReadOnlyList<string> grantedWritePaths,
        CancellationToken ct)
    {
        if (grantedWritePaths.Count == 0)
            return Result(SubAgentWorktreeSnapshotOutcome.NoWriteGrant);

        using var deadline = new CancellationTokenSource(_options.Deadline, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        try
        {
            var candidates = NormalizeAccessibleGrants(grantedWritePaths);
            if (candidates.Count == 0)
                return Result(SubAgentWorktreeSnapshotOutcome.NoAccessibleGrant);

            string? worktree = null;
            foreach (var candidate in candidates)
            {
                var probe = await RunAsync(candidate, ["rev-parse", "--show-toplevel"], ProbeOutputLimit, linked.Token).ConfigureAwait(false);
                if (probe.Outcome == GitSnapshotProcessOutcome.TimedOut)
                    return Result(SubAgentWorktreeSnapshotOutcome.TimedOut);
                if (probe.Outcome != GitSnapshotProcessOutcome.Succeeded)
                    continue;

                var reportedRoot = probe.StandardOutput.Trim();
                if (string.IsNullOrWhiteSpace(reportedRoot))
                    continue;
                var fullRoot = fileSystem.Path.GetFullPath(reportedRoot);
                if (PathsEqual(candidate, fullRoot))
                {
                    worktree = candidate;
                    break;
                }
            }

            if (worktree is null)
                return Result(SubAgentWorktreeSnapshotOutcome.NoGitWorktree);

            var untrackedResult = await RunAsync(
                worktree,
                ["ls-files", "--others", "--exclude-standard", "-z"],
                Math.Max(ProbeOutputLimit, _options.MaxUntrackedPaths * 1024),
                linked.Token).ConfigureAwait(false);
            if (untrackedResult.Outcome == GitSnapshotProcessOutcome.TimedOut)
                return Result(SubAgentWorktreeSnapshotOutcome.TimedOut, worktree);
            if (untrackedResult.Outcome != GitSnapshotProcessOutcome.Succeeded)
                return Result(SubAgentWorktreeSnapshotOutcome.ProcessFailed, worktree);

            var allUntracked = untrackedResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var untracked = allUntracked.Take(Math.Max(0, _options.MaxUntrackedPaths)).ToArray();
            var truncated = allUntracked.Length > untracked.Length;

            var diff = await RunAsync(
                worktree,
                ["diff", "HEAD", "--no-ext-diff", "--no-textconv", "--binary"],
                _options.MaxPatchBytes,
                linked.Token).ConfigureAwait(false);
            if (diff.Outcome == GitSnapshotProcessOutcome.TimedOut)
                return Result(SubAgentWorktreeSnapshotOutcome.TimedOut, worktree, untracked, truncated);
            if (diff.Outcome == GitSnapshotProcessOutcome.OutputLimitExceeded)
                return Result(SubAgentWorktreeSnapshotOutcome.Oversized, worktree, untracked, truncated);
            if (diff.Outcome != GitSnapshotProcessOutcome.Succeeded)
                return Result(SubAgentWorktreeSnapshotOutcome.ProcessFailed, worktree, untracked, truncated);
            if (string.IsNullOrEmpty(diff.StandardOutput))
                return Result(SubAgentWorktreeSnapshotOutcome.Clean, worktree, untracked, truncated);

            var redactedPatch = secretRedactor.Redact(diff.StandardOutput);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var patchBytes = utf8.GetByteCount(redactedPatch);
            if (patchBytes > _options.MaxPatchBytes)
                return Result(SubAgentWorktreeSnapshotOutcome.Oversized, worktree, untracked, truncated);

            var artifactRoot = ResolveArtifactRoot();
            if (!IsSafeArtifactRoot(artifactRoot))
                return Result(SubAgentWorktreeSnapshotOutcome.ArtifactWriteFailed, worktree, untracked, truncated);

            await _artifactGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                fileSystem.Directory.CreateDirectory(artifactRoot);
                if (!IsSafeArtifactRoot(artifactRoot))
                    return Result(SubAgentWorktreeSnapshotOutcome.ArtifactWriteFailed, worktree, untracked, truncated);

                SweepCore(artifactRoot, Math.Max(0, _options.MaxRetainedArtifacts - 1), linked.Token);
                var safeId = new string(subAgentId.Where(char.IsAsciiLetterOrDigit).Take(64).ToArray());
                if (safeId.Length == 0)
                    safeId = "subagent";

                var nonce = Guid.NewGuid().ToString("N");
                var artifactPath = fileSystem.Path.Combine(
                    artifactRoot,
                    $"{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}-{safeId}-{nonce}.patch");
                var tempPath = fileSystem.Path.Combine(artifactRoot, $".{nonce}.tmp");
                try
                {
                    await using (var stream = fileSystem.FileStream.New(
                        tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await using (var writer = new StreamWriter(stream, utf8))
                    {
                        await writer.WriteAsync(redactedPatch.AsMemory(), linked.Token).ConfigureAwait(false);
                        await writer.FlushAsync(linked.Token).ConfigureAwait(false);
                    }

                    SecureFilePermissions.RestrictToOwner(fileSystem, tempPath);
                    fileSystem.File.Move(tempPath, artifactPath);
                    SecureFilePermissions.RestrictToOwner(fileSystem, artifactPath);
                }
                catch
                {
                    TryDeleteFile(tempPath);
                    throw;
                }

                return Result(
                    SubAgentWorktreeSnapshotOutcome.Captured,
                    worktree,
                    untracked,
                    truncated,
                    artifactPath,
                    patchBytes);
            }
            finally
            {
                _artifactGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return Result(SubAgentWorktreeSnapshotOutcome.TimedOut);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "Failed to preserve recovery snapshot for sub-agent '{SubAgentId}'.", subAgentId);
            return Result(SubAgentWorktreeSnapshotOutcome.ArtifactWriteFailed);
        }
    }

    public async Task DeleteArtifactAsync(string artifactPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        var root = ResolveArtifactRoot();
        var fullPath = fileSystem.Path.GetFullPath(artifactPath);
        if (!IsSafeArtifactRoot(root) || !IsDirectPatchChild(root, fullPath))
            return;

        await _artifactGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TryDeleteFile(fullPath);
        }
        finally
        {
            _artifactGate.Release();
        }
    }

    public async Task<int> SweepArtifactsAsync(CancellationToken ct)
    {
        var root = ResolveArtifactRoot();
        if (!IsSafeArtifactRoot(root) || !fileSystem.Directory.Exists(root))
            return 0;

        await _artifactGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return IsSafeArtifactRoot(root)
                ? SweepCore(root, Math.Max(0, _options.MaxRetainedArtifacts), ct)
                : 0;
        }
        finally
        {
            _artifactGate.Release();
        }
    }

    private int SweepCore(string artifactRoot, int retainCount, CancellationToken ct)
    {
        var files = fileSystem.Directory.EnumerateFiles(artifactRoot, "*.patch", SearchOption.TopDirectoryOnly)
            .Take(Math.Max(0, _options.MaxSweepFiles))
            .Select(path => fileSystem.FileInfo.New(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ThenByDescending(info => info.Name, StringComparer.Ordinal)
            .ToList();
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - _options.Retention;
        var removed = 0;
        for (var index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if ((_options.Retention > TimeSpan.Zero && files[index].LastWriteTimeUtc < cutoff)
                || index >= retainCount)
            {
                try
                {
                    files[index].Delete();
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Could not prune sub-agent recovery artifact '{ArtifactPath}'.", files[index].FullName);
                }
            }
        }

        return removed;
    }

    private List<string> NormalizeAccessibleGrants(IReadOnlyList<string> grants)
    {
        var candidates = new SortedSet<string>(PathComparer);
        foreach (var grant in grants)
        {
            if (string.IsNullOrWhiteSpace(grant))
                continue;
            try
            {
                var fullPath = fileSystem.Path.GetFullPath(grant);
                if (fileSystem.Directory.Exists(fullPath))
                    candidates.Add(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }
        return [.. candidates];
    }

    private Task<GitSnapshotProcessResult> RunAsync(string directory, IReadOnlyList<string> arguments, int maxBytes, CancellationToken ct)
        => processRunner.RunAsync(new GitSnapshotProcessCall(directory, arguments, Math.Max(1, maxBytes)), ct);

    private string ResolveArtifactRoot()
        => string.IsNullOrWhiteSpace(_options.ArtifactRoot)
            ? fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "botnexus-subagent-recovery")
            : fileSystem.Path.GetFullPath(_options.ArtifactRoot);

    private bool IsSafeArtifactRoot(string path)
    {
        if (IsWithinVerifiedHome(path, botNexusHome.RootPath)
            || IsWithinVerifiedHome(path, botNexusHome.DataPath))
        {
            return false;
        }

        if (!fileSystem.Directory.Exists(path))
            return true;
        return (fileSystem.File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    }

    private static bool IsWithinVerifiedHome(string path, string homePath)
    {
        var relative = Path.GetRelativePath(homePath, path);
        return relative.Length == 0
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (fileSystem.File.Exists(path))
                fileSystem.File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete sub-agent recovery artifact '{ArtifactPath}'.", path);
        }
    }

    private bool IsDirectPatchChild(string root, string path)
        => PathsEqual(fileSystem.Path.GetDirectoryName(path) ?? string.Empty, root)
            && string.Equals(fileSystem.Path.GetExtension(path), ".patch", StringComparison.OrdinalIgnoreCase);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool PathsEqual(string left, string right) => PathComparer.Equals(
        left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static SubAgentWorktreeSnapshot Result(
        SubAgentWorktreeSnapshotOutcome outcome,
        string? worktree = null,
        IReadOnlyList<string>? untracked = null,
        bool truncated = false,
        string? artifactPath = null,
        int patchBytes = 0)
        => new(outcome, worktree, artifactPath, patchBytes, untracked ?? [], truncated);
}

internal sealed class SubAgentWorktreeSnapshotRetentionHostedService(
    ISubAgentWorktreeSnapshotService snapshots,
    IOptions<SubAgentWorktreeSnapshotOptions> optionsAccessor,
    ILogger<SubAgentWorktreeSnapshotRetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await snapshots.SweepArtifactsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sub-agent recovery artifact retention sweep failed.");
            }

            var interval = optionsAccessor.Value.SweepInterval > TimeSpan.Zero
                ? optionsAccessor.Value.SweepInterval
                : TimeSpan.FromHours(1);
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
