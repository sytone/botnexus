namespace BotNexus.Core.Abstractions;

/// <summary>Contract for persistent memory/notes storage.</summary>
public interface IMemoryStore
{
    /// <summary>Reads memory content for a given agent and key.</summary>
    Task<string?> ReadAsync(string agentName, string key, CancellationToken cancellationToken = default);

    /// <summary>Writes memory content for a given agent and key.</summary>
    Task WriteAsync(string agentName, string key, string content, CancellationToken cancellationToken = default);

    /// <summary>Appends content to memory for a given agent and key.</summary>
    Task AppendAsync(string agentName, string key, string content, CancellationToken cancellationToken = default);

    /// <summary>Deletes memory for a given agent and key.</summary>
    Task DeleteAsync(string agentName, string key, CancellationToken cancellationToken = default);

    /// <summary>Lists all memory keys for a given agent.</summary>
    Task<IReadOnlyList<string>> ListKeysAsync(string agentName, CancellationToken cancellationToken = default);
}
