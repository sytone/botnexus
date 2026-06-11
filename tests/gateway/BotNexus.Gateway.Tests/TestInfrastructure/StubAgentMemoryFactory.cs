using BotNexus.Gateway.Contracts.Memory;

namespace BotNexus.Gateway.Tests.TestInfrastructure;

/// <summary>
/// Stub IAgentMemoryFactory for tests that construct InProcessIsolationStrategy.
/// Returns a no-op IAgentMemory implementation.
/// </summary>
internal sealed class StubAgentMemoryFactory : IAgentMemoryFactory
{
    public IAgentMemory Create(string agentId, string? providerName = null) => new StubAgentMemory();
    public IReadOnlyList<string> GetRegisteredProviders() => ["markdown"];

    private sealed class StubAgentMemory : IAgentMemory
    {
        public Task<AgentMemoryContext> GetPromptContextAsync(AgentMemoryPromptRequest request, CancellationToken ct = default)
            => Task.FromResult(AgentMemoryContext.Empty);
        public Task SaveAsync(AgentMemorySaveRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentMemorySearchResult>> SearchAsync(AgentMemorySearchRequest request, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentMemorySearchResult>>([]);
        public Task<AgentMemorySearchResult?> GetAsync(string entryId, CancellationToken ct = default) => Task.FromResult<AgentMemorySearchResult?>(null);
        public Task OnSessionCompleteAsync(AgentMemorySessionEvent sessionEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task ConsolidateAsync(AgentMemoryConsolidateRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }
}
