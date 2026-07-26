using System.Diagnostics.CodeAnalysis;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Builds <see cref="AskUserPromptState"/> from the two inbound <c>ask_user</c> shapes the client sees:
/// the live <see cref="AgentStreamEvent"/> <c>UserInputRequired</c> event, and the durable
/// <c>PendingAskUserJson</c> payload persisted on the conversation row.
/// </summary>
/// <remarks>
/// <para>
/// As of #2322 this type is a thin client-side adapter. The reconciliation itself - metadata
/// preference order, choice parsing, timeout arithmetic, persisted-payload tolerance - moved to
/// <see cref="AskUserPromptNormalizer"/> in the shared dependency-free wire assembly
/// (<c>BotNexus.Domain.Wire</c>), because that logic is channel-independent and Telegram,
/// Discord, or a TUI cannot reference a Blazor client assembly. What remains here is only the
/// projection onto the client's own view model (<see cref="AskUserPromptState"/>), which carries
/// UI-only concerns such as <c>IsSubmitting</c>.
/// </para>
/// <para>
/// WASM PAYLOAD NOTE (#2329, #2334): the normalizer is reached through <c>BotNexus.Domain.Wire</c>
/// and NOT through <c>BotNexus.Domain</c>. The latter flows <c>Vogen</c> as a runtime asset, and
/// every assembly this project can reach is downloaded by the browser.
/// </para>
/// <para>
/// Behaviour is unchanged; the existing factory, hub, mobile, and hydration tests continue to
/// assert it against this same surface.
/// </para>
/// </remarks>
public static class AskUserPromptFactory
{
    /// <summary>
    /// Builds a prompt from a live <c>UserInputRequired</c> stream event, preferring the flattened
    /// <see cref="AgentStreamEvent.Metadata"/> values and falling back to the structured
    /// <see cref="AgentStreamEvent.UserInputRequest"/> payload. Returns false when the event lacks the
    /// required request id, prompt text, or input type.
    /// </summary>
    public static bool TryBuildFromStreamEvent(AgentStreamEvent evt, [NotNullWhen(true)] out AskUserPromptState? prompt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        prompt = null;
        if (!AskUserPromptNormalizer.TryReconcile(evt.Metadata, ToPrompt(evt.UserInputRequest), out var normalized))
            return false;

        prompt = ToState(normalized);
        return true;
    }

    /// <summary>
    /// Rebuilds an <see cref="AskUserPromptState"/> from the durable <c>PendingAskUserJson</c> payload
    /// (a serialized <c>AskUserRequest</c>) persisted on the conversation row, so a reloaded, newly-opened,
    /// or mobile client that missed the live <c>UserInputRequired</c> event can hydrate the inline prompt
    /// on connect (ask_user durability, #1488). Returns false when the JSON is missing, malformed, or
    /// lacks the required request id / prompt / input type.
    /// </summary>
    /// <param name="json">Raw persisted <c>AskUserRequest</c> JSON, or null/empty when no prompt is pending.</param>
    /// <param name="conversationId">The conversation being hydrated, used when the payload omits its own id.</param>
    /// <param name="prompt">The reconstructed prompt state on success.</param>
    /// <remarks>
    /// The conversation id stays a plain string on both sides of this call: the client's view
    /// models address conversations by string key, and the shared wire model does too, so no
    /// value-object conversion is needed (or possible) at this edge.
    /// </remarks>
    public static bool TryBuildFromPersistedJson(
        string? json,
        string conversationId,
        [NotNullWhen(true)] out AskUserPromptState? prompt)
    {
        prompt = null;
        var normalizedConversationId = string.IsNullOrWhiteSpace(conversationId)
            ? null
            : conversationId;

        if (!AskUserPromptNormalizer.TryBuildFromPersistedJson(json, normalizedConversationId, out var normalized))
            return false;

        prompt = ToState(normalized);
        return true;
    }

    /// <summary>
    /// Projects the client's own wire contract onto the shared prompt model so the domain
    /// normalizer can reconcile it against the event metadata.
    /// </summary>
    private static AskUserPrompt? ToPrompt(AskUserRequestPayload? payload)
    {
        if (payload is null)
            return null;

        // Required fields are validated by the normalizer against both sources together, so a
        // payload missing them is still usable as a partial fallback: placeholders here are only
        // ever surfaced when metadata supplies the real value.
        return new AskUserPrompt
        {
            RequestId = payload.RequestId ?? string.Empty,
            ConversationId = string.IsNullOrWhiteSpace(payload.ConversationId)
                ? null
                : payload.ConversationId,
            Prompt = payload.Prompt ?? string.Empty,
            InputType = payload.InputType ?? string.Empty,
            Choices = payload.Choices is { Count: > 0 }
                ? payload.Choices
                    .Where(choice => !string.IsNullOrWhiteSpace(choice.Value))
                    .Select(choice => new AskUserPromptChoice(
                        choice.Value!,
                        string.IsNullOrWhiteSpace(choice.Label) ? choice.Value! : choice.Label!,
                        choice.Description))
                    .ToList()
                : null,
            AllowMultiple = payload.AllowMultiple,
            AllowFreeForm = payload.AllowFreeForm,
            ExpiresAt = ParseExpiration(payload.Timeout)
        };
    }

    private static AskUserPromptState ToState(AskUserPrompt prompt) => new()
    {
        RequestId = prompt.RequestId,
        // Both sides key conversations by string, so this is a straight copy with a null-empty
        // normalisation. Pattern-matched rather than `??` chaining on a null-conditional to avoid
        // the shape the P9-B-2 session fence bans repo-wide.
        ConversationId = prompt is { ConversationId: { } promptConversationId } ? promptConversationId : string.Empty,
        Prompt = prompt.Prompt,
        InputType = prompt.InputType,
        Choices = prompt.Choices?
            .Select(choice => new AskUserChoiceState(choice.Value, choice.Label, choice.Description))
            .ToList(),
        AllowMultiple = prompt.AllowMultiple,
        AllowFreeForm = prompt.AllowFreeForm,
        ExpiresAt = prompt.ExpiresAt
    };

    private static DateTimeOffset? ParseExpiration(string? timeout)
    {
        if (string.IsNullOrWhiteSpace(timeout) || !TimeSpan.TryParse(timeout, out var duration))
            return null;
        return DateTimeOffset.UtcNow.Add(duration);
    }
}
