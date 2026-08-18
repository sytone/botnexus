using System.Globalization;
using System.Text;

namespace BotNexus.Gateway.Api.Export;

/// <summary>
/// Builds the download filename for an export response (issue #3278, acceptance criterion 7).
/// </summary>
/// <remarks>
/// <para>
/// The filename is derived from user-controlled text (a conversation title), so it is built by an
/// ALLOW-LIST: only ASCII letters, digits and hyphens survive. A deny-list of "characters Windows
/// rejects" would be the wrong shape here - it fails open on everything it forgot, and the union of
/// what Windows, Linux and macOS each reject is not a list anyone reliably recalls. Producing the
/// safe set directly makes the guarantee structural.
/// </para>
/// <para>
/// This also neutralises path traversal (<c>/</c>, <c>\</c>, <c>..</c>), the NTFS alternate-data-
/// stream separator (<c>:</c>), the HTTP header-injection characters (CR, LF, <c>"</c>) that would
/// otherwise let a title forge <c>Content-Disposition</c> parameters, and the reserved DOS device
/// names (<c>CON</c>, <c>NUL</c>, <c>COM1</c>…) which are unopenable on Windows even with an
/// extension.
/// </para>
/// </remarks>
public static class ExportFileName
{
    /// <summary>Maximum length of the slug portion, keeping the whole name well inside every filesystem's limit.</summary>
    private const int MaxSlugLength = 60;

    /// <summary>Slug used when the title contributes no usable characters (e.g. a title of only emoji).</summary>
    private const string FallbackSlug = "transcript";

    // Reserved DOS device names. Windows refuses these as a filename stem regardless of extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Builds a download filename of the form <c>&lt;slug&gt;-&lt;yyyy-MM-dd&gt;.&lt;extension&gt;</c>.
    /// </summary>
    /// <param name="title">The conversation title or other display text to slugify; may be null or empty.</param>
    /// <param name="generatedAt">The export instant, contributing the date component.</param>
    /// <param name="extension">File extension without the leading dot (e.g. <c>md</c>, <c>html</c>).</param>
    /// <returns>A filename safe on Windows, Linux and macOS.</returns>
    public static string Build(string? title, DateTimeOffset generatedAt, string extension)
    {
        var slug = Slugify(title);
        return $"{slug}-{generatedAt:yyyy-MM-dd}.{extension}";
    }

    /// <summary>
    /// Reduces arbitrary text to a lowercase, hyphen-separated ASCII slug.
    /// </summary>
    /// <param name="value">The text to slugify; may be null.</param>
    /// <returns>A non-empty slug containing only <c>[a-z0-9-]</c>.</returns>
    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return FallbackSlug;

        // Decompose accents so "Café" contributes "cafe" rather than dropping the accented letter.
        var normalized = value.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(ch);
            else if (ch is >= 'A' and <= 'Z')
                sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');

        if (slug.Length > MaxSlugLength)
            slug = slug[..MaxSlugLength].Trim('-');

        if (slug.Length == 0)
            return FallbackSlug;

        // A stem that collides with a DOS device name is unopenable on Windows; prefix it instead.
        if (ReservedNames.Contains(slug))
            slug = $"{FallbackSlug}-{slug}";

        return slug;
    }
}
