namespace BotNexus.Prompts;

public static class PromptText
{
    public static string NormalizeStructuredSection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').Select(static line => line.TrimEnd());
        return string.Join("\n", lines).Trim();
    }

    public static IReadOnlyList<string> NormalizeCapabilityIds(IEnumerable<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return capabilities
            .Select(capability => capability.Trim().ToLowerInvariant())
            .Where(static capability => capability.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static capability => capability, StringComparer.Ordinal)
            .ToList();
    }
}