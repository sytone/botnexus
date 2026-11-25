namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// A conditional metadata transition associated with a successful history replacement.
/// A null replacement removes the key; the expected presence distinguishes null from absence.
/// </summary>
/// <param name="Key">Metadata key to change.</param>
/// <param name="ExpectedPresent">Whether the key existed when compaction began.</param>
/// <param name="ExpectedValue">Original object, used to detect intervening replacements.</param>
/// <param name="Replacement">New value, or null to remove the key.</param>
public sealed record CompactionMetadataChange(
    string Key, bool ExpectedPresent, object? ExpectedValue, object? Replacement);
