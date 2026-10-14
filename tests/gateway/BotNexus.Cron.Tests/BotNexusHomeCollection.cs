namespace BotNexus.Cron.Tests;

/// <summary>
/// Serialises ambient-home fixtures within this assembly and prevents overlap with readers
/// in other collections. BOTNEXUS_HOME is process-wide, not scoped to a fixture (#4817).
/// </summary>
[CollectionDefinition(BotNexusHomeCollection.Name, DisableParallelization = true)]
public sealed class BotNexusHomeCollection
{
    /// <summary>The shared collection for test classes that change the process home.</summary>
    public const string Name = "BOTNEXUS_HOME";
}
