using BotNexus.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent;

/// <summary>
/// Reads and writes memory files for agents.
/// Memory is stored as plain text files in the agent's workspace.
/// </summary>
public sealed class MemoryStore : IMemoryStore
{
    private readonly string _basePath;
    private readonly ILogger<MemoryStore> _logger;

    public MemoryStore(string basePath, ILogger<MemoryStore> logger)
    {
        _basePath = basePath;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string?> ReadAsync(string agentName, string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(agentName, key);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string agentName, string key, string content, CancellationToken cancellationToken = default)
    {
        var path = GetPath(agentName, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Memory written: {AgentName}/{Key}", agentName, key);
    }

    /// <inheritdoc/>
    public async Task AppendAsync(string agentName, string key, string content, CancellationToken cancellationToken = default)
    {
        var path = GetPath(agentName, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string agentName, string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(agentName, key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListKeysAsync(string agentName, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_basePath, agentName, "memory");
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>([]);

        var keys = Directory.GetFiles(dir)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(k => k is not null)
            .Cast<string>()
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    private string GetPath(string agentName, string key)
        => Path.Combine(_basePath, agentName, "memory", $"{key}.txt");
}
