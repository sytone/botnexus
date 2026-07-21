using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Represents tool call id extensions.
/// </summary>
public static partial class ToolCallIdExtensions
{
    /// <summary>
    /// Executes normalize tool call id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="maxLength">The max length.</param>
    /// <returns>The normalize tool call id result.</returns>
    public static string NormalizeToolCallId(this string id, int maxLength)
    {
        var normalized = NonAlphanumericRegex().Replace(id, "_");
        return normalized.Length > maxLength ? normalized[..maxLength] : normalized;
    }

    /// <summary>
    /// Determines whether a provider or model identity belongs to the Mistral model family.
    /// </summary>
    /// <param name="model">The model whose provider and model identifiers describe the wire target.</param>
    /// <returns><see langword="true"/> when the target requires Mistral tool-call identifiers.</returns>
    public static bool IsMistralFamily(this LlmModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.Equals(model.Provider, "mistral", StringComparison.OrdinalIgnoreCase))
            return true;

        return MistralFamilyNames.Any(name => model.Id.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Converts an identifier to Mistral's exact nine-character alphanumeric wire format.
    /// </summary>
    /// <param name="id">The canonical tool-call identifier stored in conversation history.</param>
    /// <returns>A deterministic nine-character identifier safe for Mistral-compatible APIs.</returns>
    public static string NormalizeMistralToolCallId(this string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var alphanumeric = NonMistralToolCallIdCharacterRegex().Replace(id, string.Empty);
        return alphanumeric[..Math.Min(9, alphanumeric.Length)].PadRight(9, '0');
    }

    private static readonly string[] MistralFamilyNames =
        ["mistral", "devstral", "codestral", "pixtral", "mixtral"];

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonMistralToolCallIdCharacterRegex();
}
