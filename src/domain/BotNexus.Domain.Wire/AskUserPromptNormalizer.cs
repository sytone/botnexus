using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Channel-agnostic reconciliation of the two inbound <c>ask_user</c> shapes into the single
/// render-ready <see cref="AskUserPrompt"/> model (#2322).
/// </summary>
/// <remarks>
/// <para>
/// The logic here was previously owned by <c>AskUserPromptFactory</c> in the SignalR Blazor
/// client. That made it unreachable from any other channel: Telegram, Discord, or a TUI cannot
/// reference a Blazor WebAssembly client assembly, so each would have had to reimplement the
/// metadata/payload preference order, choice parsing, and timeout arithmetic - and the copies
/// would drift. It now lives in a shared assembly so every channel reconciles identically.
/// </para>
/// <para>
/// WASM PAYLOAD NOTE (#2329, #2334): this lives in the dependency-free
/// <c>BotNexus.Domain.Wire</c> rather than <c>BotNexus.Domain</c>, because the Blazor
/// WebAssembly client is one of the channels that reconciles prompts, and every assembly it can
/// reach is downloaded by the browser. <c>BotNexus.Domain</c> flows <c>Vogen</c> as a runtime
/// asset, so hosting this logic there would have put <c>Vogen.SharedTypes.dll</c> into the
/// payload. Conversation ids are therefore handled as plain strings here; the gateway maps them
/// to the typed value object at its own boundary via <c>AskUserPromptProjection</c>.
/// </para>
/// <para>
/// Preference order matches the shipped client behaviour exactly: flattened stream-event
/// metadata wins over the structured payload, because the metadata is the flattened projection
/// the gateway emits last and is therefore the most specific. Choices are only taken from
/// metadata when metadata actually yields at least one usable choice.
/// </para>
/// </remarks>
public static class AskUserPromptNormalizer
{
    private static readonly JsonSerializerOptions PersistedAskUserJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reconciles flattened stream-event metadata against an already-projected structured
    /// fallback, preferring metadata values field by field.
    /// </summary>
    /// <param name="metadata">Flattened <c>UserInputRequired</c> event metadata, when present.</param>
    /// <param name="fallback">
    /// Structured payload projected to <see cref="AskUserPrompt"/> - for the gateway this comes
    /// from <c>AskUserRequest</c> via <c>AskUserPromptProjection.ToPrompt</c>; for a client it
    /// comes from its own wire contract. May be null when only metadata is available.
    /// </param>
    /// <param name="prompt">The reconciled prompt on success.</param>
    /// <returns>
    /// <c>false</c> when neither source supplies the required request id, prompt text, and input type.
    /// </returns>
    public static bool TryReconcile(
        IReadOnlyDictionary<string, JsonElement>? metadata,
        AskUserPrompt? fallback,
        [NotNullWhen(true)] out AskUserPrompt? prompt)
    {
        prompt = null;

        var requestId = GetRequiredString(metadata, "requestId") ?? fallback?.RequestId;
        // Pattern-matched rather than `?.` so this reads the optional prompt id without the
        // null-conditional shape the P9-B-2 session fence bans repo-wide.
        var fallbackConversationId = fallback is { ConversationId: { } fallbackId } ? fallbackId : null;
        var conversationId = GetRequiredString(metadata, "conversationId") ?? fallbackConversationId;
        var promptText = GetRequiredString(metadata, "prompt") ?? fallback?.Prompt;
        var inputType = GetRequiredString(metadata, "inputType") ?? fallback?.InputType;

        if (string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(promptText) ||
            string.IsNullOrWhiteSpace(inputType))
        {
            return false;
        }

        var timeout = GetString(metadata, "timeout");
        var expiresAt = timeout is null ? fallback?.ExpiresAt : ParseExpiration(timeout);

        prompt = new AskUserPrompt
        {
            RequestId = requestId,
            // Left null when neither source carried an id. The gateway validates it when it maps
            // this wire shape onto the typed ConversationId at its own boundary.
            ConversationId = conversationId,
            Prompt = promptText,
            InputType = inputType,
            Choices = ParseChoices(metadata) ?? fallback?.Choices,
            AllowMultiple = GetBool(metadata, "allowMultiple") ?? fallback?.AllowMultiple ?? false,
            AllowFreeForm = GetBool(metadata, "allowFreeForm") ?? fallback?.AllowFreeForm ?? false,
            ExpiresAt = expiresAt
        };
        return true;
    }

    /// <summary>
    /// Rebuilds a prompt from the durable <c>PendingAskUserJson</c> payload persisted on the
    /// conversation row, so a channel that missed (or cannot replay) the live
    /// <c>UserInputRequired</c> event can still render the pending prompt on connect (#1488).
    /// </summary>
    /// <param name="json">Raw persisted <c>AskUserRequest</c> JSON, or null/empty when nothing is pending.</param>
    /// <param name="conversationId">Conversation being hydrated, used when the payload omits its own id.</param>
    /// <param name="prompt">The reconstructed prompt on success.</param>
    /// <returns><c>false</c> when the JSON is missing, malformed, or lacks required fields.</returns>
    public static bool TryBuildFromPersistedJson(
        string? json,
        string? conversationId,
        [NotNullWhen(true)] out AskUserPrompt? prompt)
    {
        prompt = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        PersistedAskUserPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PersistedAskUserPayload>(json, PersistedAskUserJsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null)
            return false;

        if (string.IsNullOrWhiteSpace(payload.RequestId) ||
            string.IsNullOrWhiteSpace(payload.Prompt) ||
            string.IsNullOrWhiteSpace(payload.InputType))
        {
            return false;
        }

        // Prefer the conversation id carried in the payload, falling back to the conversation
        // being hydrated so the prompt always binds to the thread the user is looking at.
        var resolvedConversationId = string.IsNullOrWhiteSpace(payload.ConversationId)
            ? conversationId
            : payload.ConversationId;

        prompt = new AskUserPrompt
        {
            RequestId = payload.RequestId!,
            ConversationId = resolvedConversationId,
            Prompt = payload.Prompt!,
            InputType = payload.InputType!,
            Choices = ProjectChoices(payload.Choices),
            AllowMultiple = payload.AllowMultiple,
            AllowFreeForm = payload.AllowFreeForm,
            ExpiresAt = ParseExpiration(payload.Timeout)
        };
        return true;
    }

    private static IReadOnlyList<AskUserPromptChoice>? ProjectChoices(IReadOnlyList<PersistedAskUserChoice>? choices)
    {
        if (choices is null || choices.Count == 0)
            return null;

        var projected = choices
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Value))
            .Select(choice => new AskUserPromptChoice(
                choice.Value!,
                string.IsNullOrWhiteSpace(choice.Label) ? choice.Value! : choice.Label!,
                choice.Description))
            .ToList();

        return projected.Count == 0 ? null : projected;
    }

    private static string? GetRequiredString(IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        var value = GetString(metadata, key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetString(IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var raw))
            return null;
        return raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.ToString();
    }

    private static bool? GetBool(IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var raw))
            return null;
        return raw.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(raw.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static IReadOnlyList<AskUserPromptChoice>? ParseChoices(IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("choices", out var rawChoices))
            return null;

        var parsed = ParseChoicesFromJson(rawChoices);
        return parsed is { Count: > 0 } ? parsed : null;
    }

    private static IReadOnlyList<AskUserPromptChoice>? ParseChoicesFromJson(JsonElement rawChoices)
    {
        JsonElement choicesElement;
        if (rawChoices.ValueKind == JsonValueKind.String)
        {
            var rawString = rawChoices.GetString();
            if (string.IsNullOrWhiteSpace(rawString))
                return null;
            try
            {
                using var document = JsonDocument.Parse(rawString);
                choicesElement = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        else
        {
            choicesElement = rawChoices;
        }

        if (choicesElement.ValueKind != JsonValueKind.Array)
            return null;

        var choices = new List<AskUserPromptChoice>();
        foreach (var choice in choicesElement.EnumerateArray())
        {
            var value = choice.TryGetProperty("value", out var valueElement)
                ? valueElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var label = choice.TryGetProperty("label", out var labelElement)
                ? labelElement.GetString()
                : null;
            var description = choice.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString()
                : null;

            choices.Add(new AskUserPromptChoice(
                value,
                string.IsNullOrWhiteSpace(label) ? value : label,
                description));
        }

        return choices;
    }

    private static DateTimeOffset? ParseExpiration(string? timeout)
    {
        if (string.IsNullOrWhiteSpace(timeout) || !TimeSpan.TryParse(timeout, out var duration))
            return null;
        return DateTimeOffset.UtcNow.Add(duration);
    }

    /// <summary>
    /// Loose wire shape for the persisted <c>AskUserRequest</c> JSON. Deliberately all-nullable and
    /// string-typed so a malformed or forward-versioned payload fails the field checks above rather
    /// than throwing during deserialization.
    /// </summary>
    private sealed record PersistedAskUserPayload
    {
        public string? RequestId { get; init; }
        public string? ConversationId { get; init; }
        public string? Prompt { get; init; }
        public string? InputType { get; init; }
        public IReadOnlyList<PersistedAskUserChoice>? Choices { get; init; }
        public bool AllowMultiple { get; init; }
        public bool AllowFreeForm { get; init; }
        public string? Timeout { get; init; }
    }

    private sealed record PersistedAskUserChoice
    {
        public string? Value { get; init; }
        public string? Label { get; init; }
        public string? Description { get; init; }
    }
}
