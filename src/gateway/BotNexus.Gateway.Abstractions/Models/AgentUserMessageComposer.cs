using System.Text;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Utilities;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// The single shared seam that turns raw inbound message text plus transport
/// <see cref="MessageContentPart"/> attachments into an <see cref="AgentUserMessage"/> the agent
/// loop actually consumes.
/// </summary>
/// <remarks>
/// <para>
/// This logic previously lived inline in <c>GatewayHost.BuildUserMessage</c> and was reachable from
/// exactly one dispatch path (the normal send). #2294 fixed non-image attachment loss there; #2484
/// showed the same loss recurring on steer, redirect and follow-up because those paths never
/// reached that call site at all. Rather than copy the folding logic a further three times (this
/// repository already carries an N-private-copies problem, see #2442), every dispatch path now
/// composes through this one type.
/// </para>
/// <para>
/// Composition rules: <c>image/*</c> parts travel the vision path as
/// <see cref="AgentImageContent"/>; text parts are inlined verbatim into the message text inside a
/// labelled <c>&lt;attachment&gt;</c> block; non-image binary/reference parts are surfaced as a
/// metadata reference line so the agent is at least aware of them.
/// </para>
/// </remarks>
public static class AgentUserMessageComposer
{
    /// <summary>Drop-site identifier for the text-part inlining branch (#2568 reporting).</summary>
    public const string TextDropSite = "composer.text";

    /// <summary>Drop-site identifier for the binary-part branch (#2568 reporting).</summary>
    public const string BinaryDropSite = "composer.binary";

    /// <summary>
    /// Composes an <see cref="AgentUserMessage"/> from message text and optional content parts,
    /// inlining non-image attachments into the text and routing image parts to the vision payload.
    /// </summary>
    /// <param name="content">The raw user message text.</param>
    /// <param name="contentParts">Optional transport content parts (attachments).</param>
    /// <returns>A user message carrying every supplied part in one form or another.</returns>
    public static AgentUserMessage Compose(string content, IReadOnlyList<MessageContentPart>? contentParts)
    {
        var text = AppendNonImageAttachments(content, contentParts);
        var images = BuildImageContent(contentParts);
        return images is { Count: > 0 }
            ? new AgentUserMessage(text, images)
            : new AgentUserMessage(text);
    }

    /// <summary>
    /// Inlines non-image attachment content into the user message text so it reaches the agent.
    /// Text content parts (e.g. an uploaded <c>.log</c> / <c>text/plain</c> file) are embedded
    /// verbatim inside a labelled <c>&lt;attachment&gt;</c> block. Non-image binary parts, which
    /// cannot be represented as text, are surfaced as a metadata reference line (filename, MIME
    /// type, size) so the agent is at least aware of them. Image parts are intentionally skipped
    /// here — they travel the vision path via <see cref="BuildImageContent"/>. Returns the
    /// original content unchanged when there are no non-image parts.
    /// </summary>
    /// <param name="content">The raw user message text.</param>
    /// <param name="contentParts">Optional transport content parts (attachments).</param>
    /// <returns>The message text with non-image attachments folded in.</returns>
    public static string AppendNonImageAttachments(
        string content,
        IReadOnlyList<MessageContentPart>? contentParts)
    {
        if (contentParts is null or { Count: 0 })
            return content;

        StringBuilder? sb = null;
        foreach (var part in contentParts)
        {
            switch (part)
            {
                case TextContentPart text:
                {
                    sb ??= new StringBuilder();
                    var bounded = TextualMimeType.BoundText(text.Text, out var textTruncated);
                    if (textTruncated)
                    {
                        AttachmentPayloadGuard.ReportTruncated(
                            fileName: null,
                            text.MimeType,
                            Encoding.UTF8.GetByteCount(text.Text ?? string.Empty),
                            TextualMimeType.MaxInlineBytes,
                            TextDropSite);
                    }

                    sb.Append('\n');
                    sb.Append("<attachment mimeType=\"").Append(text.MimeType).Append("\">\n");
                    sb.Append(bounded);
                    sb.Append("\n</attachment>");
                    break;
                }
                // #2568: a binary part whose MIME type is TEXTUAL (application/json, +xml, ...) is
                // decoded and inlined exactly like a TextContentPart, using the ONE shared
                // TextualMimeType predicate so the client and this composer cannot disagree about
                // what "textual" means. Genuinely opaque binaries fall through to the
                // metadata-only branch below - base64-inlining a PDF would be worse than the bug.
                case BinaryContentPart textualBin
                    when TextualMimeType.IsTextual(textualBin.MimeType):
                {
                    sb ??= new StringBuilder();
                    var name = string.IsNullOrWhiteSpace(textualBin.FileName) ? "(unnamed)" : textualBin.FileName;
                    var decoded = TextualMimeType.DecodeBounded(textualBin.Data, out var binTruncated);
                    if (binTruncated)
                    {
                        AttachmentPayloadGuard.ReportTruncated(
                            name,
                            textualBin.MimeType,
                            textualBin.Data.Length,
                            TextualMimeType.MaxInlineBytes,
                            BinaryDropSite);
                    }

                    sb.Append('\n');
                    sb.Append("<attachment fileName=\"").Append(name)
                      .Append("\" mimeType=\"").Append(textualBin.MimeType)
                      .Append("\" sizeBytes=\"").Append(textualBin.Data.Length)
                      .Append("\">\n");
                    sb.Append(decoded);
                    sb.Append("\n</attachment>");
                    break;
                }
                case BinaryContentPart bin when !bin.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                {
                    sb ??= new StringBuilder();
                    var name = string.IsNullOrWhiteSpace(bin.FileName) ? "(unnamed)" : bin.FileName;

                    // #2568 / AC5: the withholding is legitimate for an opaque binary, but the
                    // SILENCE was the defect. Report it in the ImageModalityGuard (#2485) shape.
                    AttachmentPayloadGuard.ReportWithheld(
                        name,
                        bin.MimeType,
                        bin.Data.Length,
                        BinaryDropSite);

                    sb.Append('\n');
                    sb.Append("<attachment fileName=\"").Append(name)
                      .Append("\" mimeType=\"").Append(bin.MimeType)
                      .Append("\" sizeBytes=\"").Append(bin.Data.Length)
                      .Append("\" />");
                    break;
                }
                case ReferenceContentPart refPart when !refPart.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                {
                    sb ??= new StringBuilder();
                    var name = string.IsNullOrWhiteSpace(refPart.FileName) ? "(unnamed)" : refPart.FileName;
                    sb.Append('\n');
                    sb.Append("<attachment fileName=\"").Append(name)
                      .Append("\" mimeType=\"").Append(refPart.MimeType)
                      .Append("\" uri=\"").Append(refPart.Uri)
                      .Append("\" />");
                    break;
                }
            }
        }

        return sb is null ? content : content + sb.ToString();
    }

    /// <summary>
    /// Projects <c>image/*</c> content parts onto the multimodal vision payload. Inline binaries
    /// become base64 data URIs; references pass their URI through. Returns <see langword="null"/>
    /// when there are no image parts.
    /// </summary>
    /// <param name="contentParts">Optional transport content parts (attachments).</param>
    /// <returns>The image payloads, or <see langword="null"/> when there are none.</returns>
    public static IReadOnlyList<AgentImageContent>? BuildImageContent(
        IReadOnlyList<MessageContentPart>? contentParts)
    {
        if (contentParts is null or { Count: 0 })
            return null;

        List<AgentImageContent>? images = null;
        foreach (var part in contentParts)
        {
            AgentImageContent? imageContent = part switch
            {
                // Inline binary - convert to base64 data URI
                BinaryContentPart bin when bin.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    => new AgentImageContent($"data:{bin.MimeType};base64,{Convert.ToBase64String(bin.Data)}"),
                // External URL reference
                ReferenceContentPart refPart when refPart.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    => new AgentImageContent(refPart.Uri),
                _ => null
            };

            if (imageContent is not null)
            {
                images ??= [];
                images.Add(imageContent);
            }
        }

        return images;
    }
}
