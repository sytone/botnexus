using System.Diagnostics;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Single decision point for "may this model receive image content parts, and if not, say so loudly".
/// <para>
/// Every provider message converter previously repeated the same silent test — a bare
/// <c>model.Input.Contains("image")</c> guard that dropped <see cref="ImageContent"/> blocks on the
/// floor with no log, no span event, and no user-visible signal (#2485). A vision-capable model whose
/// declared input modalities are narrowed at runtime by re-registration therefore stops accepting
/// images with nothing anywhere to explain it.
/// </para>
/// <para>
/// This type is the one shared helper for that decision (deliberately one, not the N private copies
/// documented by #2442). It does not change modality resolution or capability declarations: a
/// text-only model still drops the images, because failing the request hard would break every setup
/// that currently works. It only guarantees the drop is <em>reported</em> — as a structured
/// <see cref="LogLevel.Warning"/> and as an <see cref="ActivityEvent"/> on the provider span, so the
/// drop is observable even on transports that have no logger threaded through them.
/// </para>
/// </summary>
public static class ImageModalityGuard
{
    /// <summary>The image input modality token used in <see cref="LlmModel.Input"/>.</summary>
    public const string ImageModality = "image";

    /// <summary>
    /// The exact warning message template emitted when image parts are dropped. Kept public so tests
    /// assert the specific message rather than "some warning was logged".
    /// </summary>
    public const string DropWarningTemplate =
        "Dropping {DroppedImageCount} image content part(s) at {DropSite}: model {ModelId} " +
        "(provider {Provider}, api {Api}) does not declare the '{ImageModality}' input modality. " +
        "The request will be sent without the image(s). Declared input modalities: {DeclaredModalities}.";

    /// <summary>The name of the activity event recorded on the provider span for a drop.</summary>
    public const string DropActivityEventName = "botnexus.provider.image_parts_dropped";

    /// <summary>
    /// Format string for the in-band notice substituted for the dropped image parts (#2485 AC4).
    /// <para>
    /// AC1-AC3 made the drop observable to an <em>operator</em> (log warning + span event). AC4 is a
    /// different requirement: the <em>user</em> must be able to tell "the platform lost my image"
    /// from "this model cannot accept images". A log line they will never read does not do that.
    /// </para>
    /// <para>
    /// The converters are static, run below the session/channel layer and have no route to the
    /// portal, so there is no transport event they could raise. What they DO own is the content
    /// array being sent to the model. Substituting a text part for the removed image parts puts the
    /// explanation in the one place guaranteed to reach the user: the conversation itself. The agent
    /// sees why the promised attachment is absent and can say so, instead of confabulating about an
    /// image it never received.
    /// </para>
    /// <para>
    /// Placeholders: <c>{0}</c> dropped count, <c>{1}</c> model id, <c>{2}</c> provider.
    /// </para>
    /// </summary>
    public const string DropNoticeFormat =
        "[botnexus] {0} image attachment(s) accompanying this message were not delivered: model " +
        "'{1}' (provider '{2}') does not accept image input. The attachment(s) reached the platform " +
        "but cannot be shown to this model. Do not guess at their contents - tell the user the " +
        "image could not be delivered and that a vision-capable model is required to read it.";

    /// <summary>
    /// Builds the in-band user-visible notice describing an image drop, or <see langword="null"/>
    /// when <paramref name="imageCount"/> is not positive (so the common text-only path adds
    /// nothing to the request).
    /// </summary>
    /// <param name="model">The resolved model that cannot accept the images.</param>
    /// <param name="imageCount">How many image content parts were discarded.</param>
    /// <returns>The notice text to emit as a text content part, or null when there is nothing to say.</returns>
    public static string? BuildDropNotice(LlmModel model, int imageCount)
    {
        ArgumentNullException.ThrowIfNull(model);

        return imageCount <= 0
            ? null
            : string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                DropNoticeFormat,
                imageCount,
                model.Id,
                model.Provider);
    }

    /// <summary>
    /// True when <paramref name="model"/> declares the image input modality.
    /// </summary>
    public static bool SupportsImages(LlmModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Input.Contains(ImageModality);
    }

    /// <summary>
    /// Decides whether image parts may be emitted for <paramref name="model"/>, and reports the drop
    /// when they may not. Returns <see langword="true"/> when the caller should emit the images.
    /// </summary>
    /// <param name="model">The resolved model the request will be sent to.</param>
    /// <param name="imageCount">How many image content parts the caller is about to emit.</param>
    /// <param name="dropSite">
    /// Stable identifier of the conversion seam (e.g. <c>completions.user</c>), so an operator can tell
    /// which converter dropped the parts.
    /// </param>
    /// <param name="logger">
    /// Logger to warn on. When null the ambient provider logger factory is used, so production paths
    /// that do not thread a logger still warn instead of dropping silently.
    /// </param>
    public static bool AllowImages(LlmModel model, int imageCount, string dropSite, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(dropSite);

        if (SupportsImages(model))
            return true;

        if (imageCount > 0)
            ReportDropped(model, imageCount, dropSite, logger);

        return false;
    }

    /// <summary>
    /// Emits the structured warning and provider-span event for a confirmed image drop.
    /// </summary>
    public static void ReportDropped(LlmModel model, int imageCount, string dropSite, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(dropSite);

        if (imageCount <= 0)
            return;

        var declared = model.Input.Count == 0 ? "(none)" : string.Join(",", model.Input);
        var sink = logger ?? ProviderDiagnostics.CreateLogger(nameof(ImageModalityGuard));

        sink.LogWarning(
            DropWarningTemplate,
            imageCount,
            dropSite,
            model.Id,
            model.Provider,
            model.Api,
            ImageModality,
            declared);

        Activity.Current?.AddEvent(new ActivityEvent(
            DropActivityEventName,
            tags: new ActivityTagsCollection
            {
                { "botnexus.image.dropped_count", imageCount },
                { "botnexus.image.drop_site", dropSite },
                { "botnexus.model", model.Id },
                { "botnexus.provider.name", model.Provider },
                { "botnexus.model.api", model.Api },
                { "botnexus.model.input_modalities", declared }
            }));
    }
}
