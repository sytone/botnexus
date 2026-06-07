namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Resolves prompt section overrides from external sources (e.g. filesystem).
/// When an override is found for a section, its content replaces the built-in default.
/// </summary>
public interface IPromptOverrideResolver
{
    /// <summary>
    /// Attempts to resolve an override for the given section identifier.
    /// </summary>
    /// <param name="sectionId">The stable section identifier (e.g. "tool-enforcement", "shell-efficiency").</param>
    /// <param name="modelFamily">The current model family (e.g. "claude", "gpt"). Used for model-specific overrides.</param>
    /// <returns>The override content lines if found; <c>null</c> if no override exists.</returns>
    IReadOnlyList<string>? TryResolveOverride(string sectionId, string? modelFamily = null);
}
