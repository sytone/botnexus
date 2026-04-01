using BotNexus.Tests.E2E.Infrastructure;

namespace BotNexus.Tests.E2E.Tests;

/// <summary>
/// Shared collection fixture for all cron system E2E tests.
/// All tests in this collection share a single Gateway instance with
/// cron enabled, mock channels, and deterministic LLM provider.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CronE2eCollection : ICollectionFixture<CronFixture>
{
    public const string Name = "cron-e2e";
}
