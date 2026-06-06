namespace BotNexus.Yaml;

/// <summary>
/// Minimal YAML frontmatter parser for BotNexus skill files.
/// Handles the strict subset of YAML actually used in skill files:
/// <list type="bullet">
///   <item>Plain scalar values: <c>key: value</c></item>
///   <item>Double-quoted values: <c>key: "quoted string"</c></item>
///   <item>Single-quoted values: <c>key: 'quoted string'</c></item>
///   <item>Literal block scalars: <c>key: |</c> (newlines preserved)</item>
///   <item>Folded block scalars: <c>key: &gt;</c> (newlines converted to spaces)</item>
///   <item>Nested sections: <c>parentKey:</c> with indented key-value children (via ParseNested)</item>
///   <item>Comments: lines starting with <c>#</c> are ignored</item>
/// </list>
/// Not supported: anchors, aliases, multi-document, YAML sequences.
/// </summary>
public sealed class SimpleYamlFrontmatterParser : IYamlFrontmatterParser
{
    /// <summary>Singleton instance for use in DI-free contexts.</summary>
    public static readonly SimpleYamlFrontmatterParser Instance = new();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Parse(string frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        return ParseTopLevel(frontmatter);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ParseNested(string frontmatter, string parentKey)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentKey);
        return ExtractNestedBlock(frontmatter, parentKey);
    }

    // ── Top-level parsing ────────────────────────────────────────────────────

    private static Dictionary<string, string> ParseTopLevel(string frontmatter)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines  = SplitLines(frontmatter);

        string? pendingBlockKey     = null;
        bool    pendingBlockFolded  = false;
        var     pendingBlockLines   = new List<string>();

        void FlushPendingBlock()
        {
            if (pendingBlockKey is null) return;
            var value = pendingBlockFolded
                ? string.Join(" ", pendingBlockLines).TrimEnd()
                : string.Join("\n", pendingBlockLines).TrimEnd();
            if (!string.IsNullOrWhiteSpace(value))
                result[pendingBlockKey] = value;
            pendingBlockKey = null;
            pendingBlockLines.Clear();
        }

        foreach (var line in lines)
        {
            // Skip comment-only lines at top level
            var trimmedLine = line.TrimStart();
            if (trimmedLine.StartsWith('#'))
                continue;

            // Collecting indented block scalar content
            if (pendingBlockKey is not null)
            {
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    pendingBlockLines.Add(line.TrimStart());
                    continue;
                }
                // First non-indented, non-empty line ends the block
                FlushPendingBlock();
            }

            // Skip lines that are indented (not top-level key-value pairs)
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;

            var sep = line.IndexOf(':');
            if (sep <= 0)
                continue;

            var key      = line[..sep].Trim();
            var rawValue = line[(sep + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(key))
                continue;

            // Block scalar indicators
            if (rawValue == "|")
            {
                pendingBlockKey    = key;
                pendingBlockFolded = false;
                pendingBlockLines.Clear();
                continue;
            }

            if (rawValue == ">")
            {
                pendingBlockKey    = key;
                pendingBlockFolded = true;
                pendingBlockLines.Clear();
                continue;
            }

            // Skip block-level parent keys with no inline value (e.g. "metadata:")
            if (string.IsNullOrWhiteSpace(rawValue))
                continue;

            result[key] = Unquote(rawValue);
        }

        FlushPendingBlock();
        return result;
    }

    // ── Nested block extraction ───────────────────────────────────────────────

    private static Dictionary<string, string> ExtractNestedBlock(string frontmatter, string parentKey)
    {
        var result  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines   = SplitLines(frontmatter);
        var inBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Detect parent key header
            if (!inBlock)
            {
                if (trimmed.Equals($"{parentKey}:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith($"{parentKey}:", StringComparison.OrdinalIgnoreCase))
                {
                    var afterColon = trimmed[(parentKey.Length + 1)..].Trim();
                    if (string.IsNullOrEmpty(afterColon))
                    {
                        inBlock = true;
                        continue;
                    }
                }
                continue;
            }

            // Exit when we hit a non-indented line (new top-level key)
            if (line.Length > 0 && line[0] != ' ' && line[0] != '\t')
            {
                inBlock = false;
                continue;
            }

            var sep = trimmed.IndexOf(':');
            if (sep <= 0) continue;

            var key   = trimmed[..sep].Trim();
            var value = trimmed[(sep + 1)..].Trim();

            if (!string.IsNullOrEmpty(key))
                result[key] = Unquote(value);
        }

        return result;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            if ((value[0] == '"'  && value[^1] == '"')  ||
                (value[0] == '\'' && value[^1] == '\''))
                return value[1..^1];
        }

        return value;
    }

    private static string[] SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.None);
}
