using System.Text.Json;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Reads the provider's own authoritative final text for a block out of a Messages-shaped
/// <c>content_block_stop</c> frame, when the frame carries one (#3336).
/// </summary>
/// <remarks>
/// <para>
/// The Responses protocol hands the final value over on a dedicated
/// <c>response.output_text.done</c> event, and <c>ResponsesStreamParser</c> has reconciled against
/// it since #2443. The Messages-shaped protocols - Anthropic direct and Copilot-Messages - put the
/// equivalent value, when they emit one at all, on the terminal <c>content_block_stop</c> frame.
/// Both spellings observed in the wild are accepted: a bare <c>text</c> property, and a
/// <c>content_block</c>/<c>delta</c> object carrying <c>text</c>.
/// </para>
/// <para>
/// <b>Absence is not a mismatch.</b> The Anthropic Messages spec does not require a final text on
/// the stop frame, so the common case is <see langword="null"/>, and
/// <c>StreamAssemblyConformance.Reconcile</c> treats null as "nothing to check against" and returns
/// the assembled text unchanged. That fail-open contract is what makes it safe to call this on
/// every stop frame: a protocol that supplies the checksum gets the protection, and one that does
/// not is left exactly as it was.
/// </para>
/// </remarks>
public static class StreamBlockFinalText
{
    /// <summary>
    /// Extracts the provider's final text for a stopped block, or <see langword="null"/> when the
    /// frame carries none.
    /// </summary>
    /// <param name="contentBlockStopFrame">The parsed <c>content_block_stop</c> JSON frame.</param>
    public static string? TryRead(JsonElement contentBlockStopFrame)
    {
        if (contentBlockStopFrame.ValueKind != JsonValueKind.Object)
            return null;

        if (TryReadText(contentBlockStopFrame, out var direct))
            return direct;

        foreach (var container in (ReadOnlySpan<string>)["content_block", "delta"])
        {
            if (contentBlockStopFrame.TryGetProperty(container, out var nested)
                && nested.ValueKind == JsonValueKind.Object
                && TryReadText(nested, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryReadText(JsonElement element, out string? text)
    {
        text = null;
        if (!element.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
            return false;

        text = textProp.GetString();
        return text is not null;
    }
}
