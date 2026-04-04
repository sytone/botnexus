using BotNexus.Tests.E2E.Infrastructure;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>
/// Shared collection fixture for all multi-agent E2E tests.
/// All tests in this collection share a single Gateway instance with
/// 5 agents, 2 mock channels, and a mock LLM provider.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MultiAgentE2eCollection : ICollectionFixture<MultiAgentFixture>
{
    public const string Name = "multi-agent-e2e";
}
