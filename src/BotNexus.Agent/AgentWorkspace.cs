using System.Collections.Concurrent;
using System.Text;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;

namespace BotNexus.Agent;

public sealed class AgentWorkspace : IAgentWorkspace
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly IReadOnlyDictionary<string, string> BootstrapFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SOUL.md"] = """
# Soul

<!-- Define this agent's core personality, values, and boundaries.
     This file is loaded into every session as part of the system prompt. -->

""",
        ["IDENTITY.md"] = """
# Identity

<!-- Describe this agent's role, communication style, and operating constraints.
     This file is loaded into every session as part of the system prompt. -->

""",
        ["USER.md"] = """
# User

<!-- Capture user-specific preferences, priorities, and collaboration expectations.
     This file is loaded into every session as part of the system prompt. -->

"""
    };

    private const string HeartbeatStub = """
# Heartbeat

<!-- Define periodic instructions for this agent.
     Example: memory consolidation cadence, integrity checks, recurring cleanup tasks. -->

""";

    public AgentWorkspace(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        AgentName = agentName;
        WorkspacePath = BotNexusHome.GetAgentWorkspacePath(agentName);
    }

    public string AgentName { get; }
    public string WorkspacePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(WorkspacePath);
        Directory.CreateDirectory(Path.Combine(WorkspacePath, "memory"));
        Directory.CreateDirectory(Path.Combine(WorkspacePath, "memory", "daily"));

        foreach (var (fileName, content) in BootstrapFiles)
            await CreateFileIfMissingAsync(fileName, content, cancellationToken).ConfigureAwait(false);

        await CreateFileIfMissingAsync("MEMORY.md", string.Empty, cancellationToken).ConfigureAwait(false);
        await CreateFileIfMissingAsync("HEARTBEAT.md", HeartbeatStub, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var filePath = ResolveWorkspaceFilePath(fileName);
        if (!File.Exists(filePath))
            return null;

        return await WithRetryAsync(async () =>
        {
            var fileLock = GetLock(filePath);
            await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await File.ReadAllTextAsync(filePath, Utf8, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                fileLock.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        var filePath = ResolveWorkspaceFilePath(fileName);
        Directory.CreateDirectory(WorkspacePath);

        await WithRetryAsync(async () =>
        {
            var fileLock = GetLock(filePath);
            await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await File.WriteAllTextAsync(filePath, content, Utf8, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                fileLock.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendFileAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        var filePath = ResolveWorkspaceFilePath(fileName);
        Directory.CreateDirectory(WorkspacePath);

        await WithRetryAsync(async () =>
        {
            var fileLock = GetLock(filePath);
            await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(filePath, content, Utf8, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                fileLock.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(WorkspacePath))
            return Task.FromResult<IReadOnlyList<string>>([]);

        var files = Directory.GetFiles(WorkspacePath, "*.md", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public bool FileExists(string fileName)
        => File.Exists(ResolveWorkspaceFilePath(fileName));

    private async Task CreateFileIfMissingAsync(string fileName, string content, CancellationToken cancellationToken)
    {
        var filePath = ResolveWorkspaceFilePath(fileName);
        if (File.Exists(filePath))
            return;

        await WithRetryAsync(async () =>
        {
            var fileLock = GetLock(filePath);
            await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(filePath))
                    await File.WriteAllTextAsync(filePath, content, Utf8, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                fileLock.Release();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static SemaphoreSlim GetLock(string filePath)
        => FileLocks.GetOrAdd(filePath, static _ => new SemaphoreSlim(1, 1));

    private string ResolveWorkspaceFilePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var normalized = fileName.Trim();
        if (!string.Equals(normalized, Path.GetFileName(normalized), StringComparison.Ordinal))
            throw new ArgumentException("File name must be a simple file name in workspace root.", nameof(fileName));

        return Path.Combine(WorkspacePath, normalized);
    }

    private static async Task WithRetryAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await WithRetryAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 50), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
