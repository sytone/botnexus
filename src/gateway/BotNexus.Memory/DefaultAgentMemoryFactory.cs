using System.IO.Abstractions;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Contracts.Memory;

namespace BotNexus.Memory;

/// <summary>
/// Default factory that creates <see cref="IAgentMemory"/> instances.
/// Currently only supports the "markdown" provider which delegates to
/// file-based workspace saves and SQLite-backed search.
/// </summary>
public sealed class DefaultAgentMemoryFactory : IAgentMemoryFactory
{
    private readonly IAgentWorkspaceManager _workspaceManager;
    private readonly IMemoryStoreFactory _memoryStoreFactory;
    private readonly IFileSystem _fileSystem;

    private static readonly IReadOnlyList<string> RegisteredProviders = ["markdown"];

    public DefaultAgentMemoryFactory(
        IAgentWorkspaceManager workspaceManager,
        IMemoryStoreFactory memoryStoreFactory,
        IFileSystem fileSystem)
    {
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _memoryStoreFactory = memoryStoreFactory ?? throw new ArgumentNullException(nameof(memoryStoreFactory));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <inheritdoc />
    public IAgentMemory Create(string agentId, string? providerName = null)
    {
        var provider = providerName ?? "markdown";

        if (!string.Equals(provider, "markdown", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Memory provider '{provider}' is not registered. Available: {string.Join(", ", RegisteredProviders)}");

        var memoryStore = _memoryStoreFactory.Create(agentId);
        _ = memoryStore.InitializeAsync(CancellationToken.None);

        return new MarkdownAgentMemory(
            agentId,
            _workspaceManager,
            memoryStore,
            _fileSystem);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetRegisteredProviders() => RegisteredProviders;
}
