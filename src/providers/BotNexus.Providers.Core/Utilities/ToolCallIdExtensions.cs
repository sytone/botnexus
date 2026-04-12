using System.Text.RegularExpressions;

namespace BotNexus.Providers.Core.Utilities;

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

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NonAlphanumericRegex();
}
