using System.Text.RegularExpressions;

namespace BotNexus.Providers.Core.Utilities;

public static partial class ToolCallIdExtensions
{
    public static string NormalizeToolCallId(this string id, int maxLength)
    {
        var normalized = NonAlphanumericRegex().Replace(id, "_");
        return normalized.Length > maxLength ? normalized[..maxLength] : normalized;
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NonAlphanumericRegex();
}
