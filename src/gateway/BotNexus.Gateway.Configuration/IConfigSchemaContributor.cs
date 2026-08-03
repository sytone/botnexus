namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Contributes a default configuration section for config hydration.
/// Implementations declare which JSON path they own and provide a default
/// object that will be deep-merged into config.json on startup when keys are missing.
/// </summary>
public interface IConfigSchemaContributor
{
    /// <summary>
    /// Dot-separated JSON path for this section (e.g. "gateway", "gateway.compaction", "cron").
    /// </summary>
    string SectionPath { get; }

    /// <summary>
    /// Returns an object representing the default values for this section.
    /// Serialized to JSON and deep-merged with existing config — existing keys are never overwritten.
    /// </summary>
    object GetDefaults();
}
