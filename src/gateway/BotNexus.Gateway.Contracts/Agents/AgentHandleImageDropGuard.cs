using System.Diagnostics;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Single decision point for "this <see cref="IAgentHandle"/> is about to degrade a composed
/// multimodal <see cref="AgentUserMessage"/> to its text, and the vision payload has nowhere to go
/// - say so loudly".
/// <para>
/// #2484 / PR #2494 added typed <see cref="AgentUserMessage"/> overloads to <see cref="IAgentHandle"/>
/// with <em>default</em> implementations that forward <c>message.Content</c> to the pre-existing
/// text-only method. That default preserves inlined non-image attachments (which
/// <c>AgentUserMessageComposer</c> folds into the text) but has no representation at all for
/// <see cref="AgentImageContent"/>: every image on a steer / redirect / follow-up issued against a
/// handle that does not override the typed method was discarded with no error, no warning and no
/// user-visible signal (#2495).
/// </para>
/// <para>
/// This helper deliberately mirrors the posture of <c>ImageModalityGuard</c> (#2485) rather than
/// inventing a second one: the degrade still happens - failing the request hard would break every
/// isolation strategy that currently works text-only - but it is now <em>reported</em>, as a
/// structured <see cref="LogLevel.Warning"/> and as an <see cref="ActivityEvent"/> on the ambient
/// span, so the loss is observable even on transports with no logger threaded through them. It
/// reuses <see cref="ProviderDiagnostics"/> for the ambient logger factory, which the API
/// composition root already assigns, so no new DI registration is required.
/// </para>
/// </summary>
public static class AgentHandleImageDropGuard
{
    /// <summary>
    /// The exact warning message template emitted when a handle degrades a multimodal message to
    /// text. Public so tests assert the specific message rather than "some warning was logged".
    /// </summary>
    public const string DropWarningTemplate =
        "Dropping {DroppedImageCount} image content part(s) at {DropSite}: agent handle " +
        "{HandleType} does not implement the typed UserMessage path, so the message is degraded " +
        "to text only and the vision payload cannot be carried. The dispatch will proceed with " +
        "text only.";

    /// <summary>The name of the activity event recorded on the ambient span for a drop.</summary>
    public const string DropActivityEventName = "botnexus.agent_handle.image_parts_dropped";

    /// <summary>
    /// Reports - and does not suppress - the vision-payload loss incurred by degrading
    /// <paramref name="message"/> to its text on <paramref name="handle"/>, then returns the text
    /// the caller should forward. A no-op (beyond returning the text) when the message carries no
    /// images, so the common text-only dispatch stays silent.
    /// </summary>
    /// <param name="handle">The handle performing the degrade, used to name the drop in the log.</param>
    /// <param name="message">The composed multimodal message being degraded.</param>
    /// <param name="dropSite">
    /// Stable identifier of the dispatch path (e.g. <c>agent_handle.steer</c>) so an operator can
    /// tell which call lost the images.
    /// </param>
    /// <param name="logger">
    /// Logger to warn on. When null the ambient provider logger factory is used, so production
    /// paths that do not thread a logger still warn instead of dropping silently.
    /// </param>
    /// <returns>The message text, to be forwarded to the text-only overload.</returns>
    public static string DegradeToText(
        IAgentHandle handle,
        AgentUserMessage message,
        string dropSite,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(dropSite);

        ReportDropped(handle.GetType().Name, message.Images?.Count ?? 0, dropSite, logger);
        return message.Content ?? string.Empty;
    }

    /// <summary>
    /// Emits the structured warning and span event for a confirmed vision-payload drop. Does
    /// nothing when <paramref name="imageCount"/> is zero or negative.
    /// </summary>
    /// <param name="handleTypeName">Name of the handle type that degraded the message.</param>
    /// <param name="imageCount">How many image content parts are being discarded.</param>
    /// <param name="dropSite">Stable identifier of the dispatch path.</param>
    /// <param name="logger">Optional logger; the ambient provider logger factory is used when null.</param>
    public static void ReportDropped(
        string handleTypeName,
        int imageCount,
        string dropSite,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handleTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dropSite);

        if (imageCount <= 0)
            return;

        var sink = logger ?? ProviderDiagnostics.CreateLogger(nameof(AgentHandleImageDropGuard));

        sink.LogWarning(DropWarningTemplate, imageCount, dropSite, handleTypeName);

        System.Diagnostics.Activity.Current?.AddEvent(new ActivityEvent(
            DropActivityEventName,
            tags: new ActivityTagsCollection
            {
                { "botnexus.image.dropped_count", imageCount },
                { "botnexus.image.drop_site", dropSite },
                { "botnexus.agent_handle.type", handleTypeName }
            }));
    }

    /// <summary>Drop site identifier for the steer dispatch path.</summary>
    public const string SteerSite = "agent_handle.steer";

    /// <summary>Drop site identifier for the interrupt-and-steer (redirect) dispatch path.</summary>
    public const string RedirectSite = "agent_handle.interrupt_and_steer";

    /// <summary>Drop site identifier for the follow-up dispatch path.</summary>
    public const string FollowUpSite = "agent_handle.follow_up";

    /// <summary>Drop site identifier for the blocking prompt dispatch path.</summary>
    public const string PromptSite = "agent_handle.prompt";

    /// <summary>Drop site identifier for the streaming dispatch path.</summary>
    public const string StreamSite = "agent_handle.stream";
}
