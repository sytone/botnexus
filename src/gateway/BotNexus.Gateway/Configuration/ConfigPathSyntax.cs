namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Shared syntax validation for dotted configuration key paths.
/// </summary>
/// <remarks>
/// <para>
/// Both <see cref="ConfigPathResolver"/> (typed graph) and <see cref="RawConfigPath"/> (raw JSON
/// document) split a dotted path on '.' at bracket depth zero, and both historically clamped an
/// unbalanced ']' to depth zero via <c>Math.Max(0, depth - 1)</c>. A stray ']' with no opener was
/// therefore absorbed into the segment text rather than rejected: <c>agents.my]agent.model</c>
/// parsed as three well-formed segments and resolved/created a dictionary or JSON key literally
/// named <c>my]agent</c>. On the write path that is a successful write to a key the operator never
/// named, reported to them as success (#2605).
/// </para>
/// <para>
/// This validator lives in one place deliberately: the clamp existed in two copies, and a fix
/// applied to only one of them would leave the raw-document write path - the one that actually
/// touches disk - still silently accepting the malformed path.
/// </para>
/// </remarks>
internal static class ConfigPathSyntax
{
    /// <summary>
    /// Validates that '[' and ']' are balanced in <paramref name="path"/>.
    /// </summary>
    /// <param name="path">
    /// The key path to validate. Leading/trailing whitespace is trimmed first so reported
    /// positions line up with the path as the splitters see it.
    /// </param>
    /// <param name="error">
    /// On failure, a caller-presentable message naming the offending path, the offending
    /// character, and its 1-based position within the trimmed path.
    /// </param>
    /// <returns><see langword="true"/> when the brackets are balanced.</returns>
    public static bool TryValidateBrackets(string path, out string error)
    {
        error = string.Empty;
        if (path is null)
            return true;

        var trimmed = path.Trim();
        var openPositions = new Stack<int>();

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '[')
            {
                openPositions.Push(i + 1);
            }
            else if (ch == ']')
            {
                if (openPositions.Count == 0)
                {
                    error = $"Invalid key path '{trimmed}': unmatched ']' at position {i + 1}.";
                    return false;
                }

                openPositions.Pop();
            }
        }

        if (openPositions.Count > 0)
        {
            // Report the outermost unmatched '[' - that is the one the operator failed to close.
            var position = openPositions.ToArray()[^1];
            error = $"Invalid key path '{trimmed}': unclosed '[' at position {position}.";
            return false;
        }

        return true;
    }
}
