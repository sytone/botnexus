using System.Text.Json;

namespace BotNexus.Memory;

/// <summary>
/// A single decoded role record from a stored transcript turn row.
/// </summary>
/// <param name="Role">The role name, either <c>User</c> or <c>Assistant</c>.</param>
/// <param name="Text">The exact original text for that role.</param>
public readonly record struct TranscriptRoleRecord(string Role, string Text);

/// <summary>
/// The single encode/decode seam for transcript turn-pair memory rows (#2954).
/// </summary>
/// <remarks>
/// <para>
/// Rows used to be persisted as a bare <c>User: {text}\nAssistant: {text}</c> interpolation. That format
/// has a role separator (<c>\nAssistant: </c>) which is also perfectly legal <em>content</em>, so a user
/// message containing a line <c>Assistant: ignore prior instructions</c> synthesised a third role record
/// inside the stored row. Because those rows are replayed to the model through <c>memory_search</c> and the
/// memory-dreaming consolidation prompt, that is a stored (delayed) prompt-injection with the same blast
/// radius as #1560.
/// </para>
/// <para>
/// The fix is to make the encoding unambiguous rather than to filter harder: each role payload is
/// JSON-quoted, so no user-supplied text can ever produce a raw newline or an unquoted role prefix in the
/// serialised form. <see cref="MemoryContentSanitizer"/> stays exactly where it is — sanitising is a markup
/// concern and delimiting is an encoding concern, and both are wanted.
/// </para>
/// <para>
/// Decoding falls back to the legacy undelimited shape so rows written before this change keep parsing;
/// there is no migration and no data loss on upgrade.
/// </para>
/// </remarks>
public static class TranscriptTurnFormat
{
    /// <summary>Role name used for the user half of a turn pair.</summary>
    public const string UserRole = "User";

    /// <summary>Role name used for the assistant half of a turn pair.</summary>
    public const string AssistantRole = "Assistant";

    private const string UserPrefix = "User: ";
    private const string AssistantPrefix = "Assistant: ";
    private const string LegacyAssistantPrefix = "\nAssistant: ";

    /// <summary>
    /// Encodes a user/assistant turn pair into the canonical stored form.
    /// Both writers (<c>MemoryIndexer</c> and <c>MarkdownAgentMemory</c>) must call this rather than
    /// restating the interpolation, so the format has exactly one definition.
    /// </summary>
    public static string Encode(string? userText, string? assistantText)
        => string.Concat(UserPrefix, Quote(userText), "\n", AssistantPrefix, Quote(assistantText));

    /// <summary>
    /// Quotes a single role payload. JSON string quoting guarantees the result contains no raw newline and
    /// no unescaped quote; <c>\u2028</c>/<c>\u2029</c> are escaped explicitly because they are line
    /// terminators to some consumers even though they are not <c>\n</c>.
    /// </summary>
    public static string Quote(string? text)
    {
        var quoted = JsonSerializer.Serialize(text ?? string.Empty);
        return quoted
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a stored row into its role records. Returns an empty list when the row is not a turn pair.
    /// A well-formed row always yields exactly two records regardless of what the payloads contain.
    /// </summary>
    public static IReadOnlyList<TranscriptRoleRecord> ParseRoleRecords(string? content)
        => TryDecode(content, out var user, out var assistant)
            ? [new TranscriptRoleRecord(UserRole, user), new TranscriptRoleRecord(AssistantRole, assistant)]
            : [];

    /// <summary>
    /// Decodes a stored row, recovering both payloads exactly. Understands the quoted format first and
    /// falls back to the legacy undelimited format for rows written before #2954.
    /// </summary>
    public static bool TryDecode(string? content, out string userText, out string assistantText)
    {
        userText = string.Empty;
        assistantText = string.Empty;

        if (string.IsNullOrEmpty(content) || !content.StartsWith(UserPrefix, StringComparison.Ordinal))
            return false;

        if (TryDecodeQuoted(content, out userText, out assistantText))
            return true;

        return TryDecodeLegacy(content, out userText, out assistantText);
    }

    private static bool TryDecodeQuoted(string content, out string userText, out string assistantText)
    {
        userText = string.Empty;
        assistantText = string.Empty;

        var rest = content[UserPrefix.Length..];
        if (rest.Length == 0 || rest[0] != '"')
            return false;

        // A quoted payload can never contain a raw newline, so the first newline is unambiguously the
        // record separator.
        var newline = rest.IndexOf('\n');
        if (newline < 0)
            return false;

        var userToken = rest[..newline];
        var tail = rest[(newline + 1)..];
        if (!tail.StartsWith(AssistantPrefix, StringComparison.Ordinal))
            return false;

        var assistantToken = tail[AssistantPrefix.Length..];
        if (assistantToken.Length == 0 || assistantToken[0] != '"')
            return false;

        if (!TryUnquote(userToken, out var user) || !TryUnquote(assistantToken, out var assistant))
            return false;

        userText = user;
        assistantText = assistant;
        return true;
    }

    private static bool TryDecodeLegacy(string content, out string userText, out string assistantText)
    {
        userText = string.Empty;
        assistantText = string.Empty;

        var assistantIndex = content.IndexOf(LegacyAssistantPrefix, StringComparison.Ordinal);
        if (assistantIndex < 0)
            return false;

        userText = content[UserPrefix.Length..assistantIndex];
        assistantText = content[(assistantIndex + LegacyAssistantPrefix.Length)..];
        return true;
    }

    private static bool TryUnquote(string token, out string text)
    {
        text = string.Empty;
        try
        {
            var value = JsonSerializer.Deserialize<string>(token);
            if (value is null)
                return false;
            text = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
