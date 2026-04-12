namespace BotNexus.Tools.Extensions;

internal static class StringExtensions
{
    internal static string NormalizeLineEndings(this string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }
}
