using System.Diagnostics;
using BotNexus.Agent.Providers.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Single decision point for "was an attachment payload withheld from the agent, and if so, say so
/// loudly".
/// </summary>
/// <remarks>
/// <para>
/// #2568: a non-image, non-<c>text/*</c> attachment (for example <c>application/json</c>) reached
/// the agent as a metadata-only <c>&lt;attachment ... /&gt;</c> tag with its payload silently
/// discarded. Nothing errored, nothing logged, and nothing surfaced in the UI, so the user believed
/// the agent had the file and the agent believed no file arrived.
/// </para>
/// <para>
/// This mirrors the <see cref="ImageModalityGuard"/> precedent established by #2485 rather than
/// inventing a second reporting shape: the same "we are not failing the request, but the drop is
/// reported" contract, the same structured <see cref="LogLevel.Warning"/> plus
/// <see cref="ActivityEvent"/> pair, the same public message/event-name constants so tests assert
/// the specific message rather than "some warning was logged", and the same ambient-logger fallback
/// via <see cref="ProviderDiagnostics"/> for static composition seams that have no logger threaded
/// through them. It does NOT duplicate that guard's role: images remain entirely
/// <see cref="ImageModalityGuard"/>'s business.
/// </para>
/// <para>
/// Withholding is still the correct behaviour for genuinely opaque binaries
/// (<c>application/pdf</c>, <c>application/zip</c>, <c>application/octet-stream</c>): base64-inlining
/// them into a prompt would be worse than the defect. Only the SILENCE was the bug.
/// </para>
/// </remarks>
public static class AttachmentPayloadGuard
{
    /// <summary>
    /// The exact warning message template emitted when an attachment payload is withheld. Public so
    /// tests assert the specific message rather than "some warning was logged".
    /// </summary>
    public const string WithheldWarningTemplate =
        "Withholding the payload of attachment '{FileName}' ({MimeType}, {SizeBytes} bytes) at " +
        "{DropSite}: the type is not recognised as textual, so only attachment metadata reaches the " +
        "agent. The agent can see that the file exists but cannot read its contents.";

    /// <summary>
    /// The exact warning message template emitted when an inlined textual payload is truncated at
    /// the bound. Public for the same reason as <see cref="WithheldWarningTemplate"/>.
    /// </summary>
    public const string TruncatedWarningTemplate =
        "Truncating the payload of attachment '{FileName}' ({MimeType}) at {DropSite}: {SizeBytes} " +
        "bytes exceeds the {MaxInlineBytes}-byte inline bound. The agent receives the leading " +
        "portion followed by an explicit truncation marker.";

    /// <summary>The activity event recorded on the current span when a payload is withheld.</summary>
    public const string WithheldActivityEventName = "botnexus.attachment.payload_withheld";

    /// <summary>The activity event recorded on the current span when a payload is truncated.</summary>
    public const string TruncatedActivityEventName = "botnexus.attachment.payload_truncated";

    /// <summary>
    /// Emits the structured warning and span event for an attachment whose payload was withheld
    /// because its MIME type is not textual.
    /// </summary>
    /// <param name="fileName">The attachment file name, or a placeholder when unnamed.</param>
    /// <param name="mimeType">The declared MIME type that failed the textual test.</param>
    /// <param name="sizeBytes">Payload size in bytes, so the loss is quantified.</param>
    /// <param name="dropSite">
    /// Stable identifier of the composition seam (e.g. <c>composer.binary</c>), so an operator can
    /// tell which branch withheld the payload.
    /// </param>
    /// <param name="logger">
    /// Logger to warn on. When null the ambient provider logger factory is used, so static
    /// composition seams still warn instead of dropping silently.
    /// </param>
    public static void ReportWithheld(
        string? fileName,
        string mimeType,
        long sizeBytes,
        string dropSite,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dropSite);

        var name = string.IsNullOrWhiteSpace(fileName) ? "(unnamed)" : fileName;
        var type = string.IsNullOrWhiteSpace(mimeType) ? "(unknown)" : mimeType;
        var sink = logger ?? ProviderDiagnostics.CreateLogger(nameof(AttachmentPayloadGuard));

        sink.LogWarning(WithheldWarningTemplate, name, type, sizeBytes, dropSite);

        Activity.Current?.AddEvent(new ActivityEvent(
            WithheldActivityEventName,
            tags: new ActivityTagsCollection
            {
                { "botnexus.attachment.file_name", name },
                { "botnexus.attachment.mime_type", type },
                { "botnexus.attachment.size_bytes", sizeBytes },
                { "botnexus.attachment.drop_site", dropSite }
            }));
    }

    /// <summary>
    /// Emits the structured warning and span event for an inlined attachment payload that hit the
    /// inline bound and was truncated.
    /// </summary>
    public static void ReportTruncated(
        string? fileName,
        string mimeType,
        long sizeBytes,
        int maxInlineBytes,
        string dropSite,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dropSite);

        var name = string.IsNullOrWhiteSpace(fileName) ? "(unnamed)" : fileName;
        var type = string.IsNullOrWhiteSpace(mimeType) ? "(unknown)" : mimeType;
        var sink = logger ?? ProviderDiagnostics.CreateLogger(nameof(AttachmentPayloadGuard));

        sink.LogWarning(TruncatedWarningTemplate, name, type, dropSite, sizeBytes, maxInlineBytes);

        Activity.Current?.AddEvent(new ActivityEvent(
            TruncatedActivityEventName,
            tags: new ActivityTagsCollection
            {
                { "botnexus.attachment.file_name", name },
                { "botnexus.attachment.mime_type", type },
                { "botnexus.attachment.size_bytes", sizeBytes },
                { "botnexus.attachment.max_inline_bytes", maxInlineBytes },
                { "botnexus.attachment.drop_site", dropSite }
            }));
    }
}
