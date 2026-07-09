using BotNexus.Extensions.Skills.Telemetry;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// Registers skill usage telemetry (#1833) so the agent-facing skill tools and the read API share a
/// single <see cref="ISkillUsageTelemetry"/> instance backed by SQLite. Runs while the host service
/// collection is still mutable (see <see cref="IServiceContributor"/>); the store is a singleton so
/// counter increments from every session accumulate into one database.
/// </summary>
public sealed class SkillsServiceContributor : IServiceContributor
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // Persist alongside the other BotNexus SQLite state under ~/.botnexus/data. The skill tool
        // contributors compute their skill directories from the same UserProfile root, so telemetry
        // lands next to the state it describes without a compile-time dependency on Gateway internals.
        services.TryAddSingleton<ISkillUsageTelemetry>(_ =>
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dbPath = Path.Combine(home, ".botnexus", "data", "skill-usage.db");
            return new SqliteSkillUsageStore(dbPath);
        });
    }
}
