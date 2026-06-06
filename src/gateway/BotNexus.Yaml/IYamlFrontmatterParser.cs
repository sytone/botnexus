namespace BotNexus.Yaml;

/// <summary>
/// Parses YAML frontmatter blocks into key-value string dictionaries.
/// Implementations must handle the YAML subset used in BotNexus skill files.
/// </summary>
public interface IYamlFrontmatterParser
{
    /// <summary>
    /// Parses a YAML frontmatter block (the text between the <c>---</c> delimiters,
    /// with the delimiters already stripped) into a key-to-value dictionary.
    /// </summary>
    /// <param name="frontmatter">
    /// The raw frontmatter text — multi-line YAML without the surrounding <c>---</c> fences.
    /// </param>
    /// <returns>
    /// A case-insensitive dictionary of top-level key-value pairs.
    /// Block scalars (<c>|</c> literal, <c>&gt;</c> folded) are supported.
    /// Quoted strings (single and double) are unquoted.
    /// Comments (<c>#</c>) are ignored.
    /// Nested block sections (e.g. <c>metadata:</c>) are NOT included in the returned
    /// dictionary — callers that need nested values should use <see cref="ParseNested"/>.
    /// </returns>
    IReadOnlyDictionary<string, string> Parse(string frontmatter);

    /// <summary>
    /// Parses the indented child key-value pairs under a named parent key
    /// (e.g. <c>metadata:</c>) from a frontmatter block.
    /// </summary>
    /// <param name="frontmatter">The raw frontmatter text (fences stripped).</param>
    /// <param name="parentKey">The top-level key whose children to extract.</param>
    /// <returns>A case-insensitive dictionary of the nested key-value pairs.</returns>
    IReadOnlyDictionary<string, string> ParseNested(string frontmatter, string parentKey);
}
