namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Portal-side <c>this string</c> text extensions (#2925).
/// </summary>
/// <remarks>
/// These deliberately do NOT live in <c>BotNexus.Domain.Text.StringTextExtensions</c>.
/// <c>BlazorClient.Core</c> is barred from referencing <c>BotNexus.Domain</c> by
/// <c>WasmPayloadDependencyArchitectureTests</c> - the whole point of that fence is to keep the
/// domain assembly out of the WASM payload - so a single shared extensions home is unreachable
/// from here by construction. This is the portal's home for the same shape of operation.
/// </remarks>
public static class PortalTextExtensions
{
    /// <summary>
    /// Collapses a value to a single line: every Unicode control character (including
    /// <c>\n</c>, <c>\r</c>, <c>\t</c>) and every whitespace run becomes exactly one space, and
    /// leading/trailing whitespace is trimmed. Null or blank input returns an empty string.
    /// </summary>
    /// <param name="value">Raw text, possibly containing control characters.</param>
    /// <returns>A trimmed single-line rendering of <paramref name="value"/>.</returns>
    public static string ToSingleLine(this string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var buffer = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            // Control characters are treated as whitespace rather than dropped so that
            // "a\tb" stays two readable words instead of collapsing into "ab".
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                pendingSpace = buffer.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                buffer.Append(' ');
                pendingSpace = false;
            }
            buffer.Append(ch);
        }
        return buffer.ToString();
    }
}
