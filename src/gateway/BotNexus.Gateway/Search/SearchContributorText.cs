using BotNexus.Domain.Text;

namespace BotNexus.Gateway.Search;

internal static class SearchContributorText
{
    internal static bool ContainsAny(string query, params string?[] values)
        => values.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    internal static string BoundedSnippet(string? value, int maxLength)
    {
        const string suffix = "...";
        return TextTruncation.SafeTruncate(value, Math.Max(0, maxLength - suffix.Length), suffix)
            ?? string.Empty;
    }
}
