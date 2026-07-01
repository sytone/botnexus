using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Builds <see cref="AskUserPromptState"/> from the two inbound <c>ask_user</c> shapes the client sees:
/// the live <see cref="AgentStreamEvent"/> <c>UserInputRequired</c> event, and the durable
/// <c>PendingAskUserJson</c> payload persisted on the conversation row.
/// </summary>
/// <remarks>
/// This parsing was extracted verbatim from <see cref="GatewayEventHandler"/> (#1753). It is a set of
/// pure functions with no dependency on client state (<c>IClientStateStore</c>), the hub connection, or a
/// logger, so it lives in its own testable home shared by both consumers -- the event handler (live path)
/// and <see cref="AgentInteractionService"/> (REST-hydrated persisted path) -- rather than being owned by
/// the stateful event handler and reached into via a <c>public static</c> method.
/// </remarks>
public static class AskUserPromptFactory
{
    private static readonly JsonSerializerOptions PersistedAskUserJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Builds a prompt from a live <c>UserInputRequired</c> stream event, preferring the flattened
    /// <see cref="AgentStreamEvent.Metadata"/> values and falling back to the structured
    /// <see cref="AgentStreamEvent.UserInputRequest"/> payload. Returns false when the event lacks the
    /// required request id, prompt text, or input type.
    /// </summary>
    public static bool TryBuildFromStreamEvent(AgentStreamEvent evt, [NotNullWhen(true)] out AskUserPromptState? prompt)
    {
        prompt = null;
        var metadata = evt.Metadata;
        var payload = evt.UserInputRequest;

        var requestId = GetRequiredString(metadata, "requestId") ?? payload?.RequestId;
        var conversationId = GetRequiredString(metadata, "conversationId") ?? payload?.ConversationId;
        var promptText = GetRequiredString(metadata, "prompt") ?? payload?.Prompt;
        var inputType = GetRequiredString(metadata, "inputType") ?? payload?.InputType;

        if (string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(promptText) ||
            string.IsNullOrWhiteSpace(inputType))
        {
            return false;
        }

        var choices = ParseChoices(metadata, payload?.Choices);
        var allowMultiple = GetBool(metadata, "allowMultiple") ?? payload?.AllowMultiple ?? false;
        var allowFreeForm = GetBool(metadata, "allowFreeForm") ?? payload?.AllowFreeForm ?? false;
        var timeout = GetString(metadata, "timeout") ?? payload?.Timeout;
        var expiresAt = ParseExpiration(timeout);

        prompt = new AskUserPromptState
        {
            RequestId = requestId,
            ConversationId = conversationId ?? string.Empty,
            Prompt = promptText,
            InputType = inputType,
            Choices = choices,
            AllowMultiple = allowMultiple,
            AllowFreeForm = allowFreeForm,
            ExpiresAt = expiresAt
        };

        return true;
    }

    /// <summary>
    /// Rebuilds an <see cref="AskUserPromptState"/> from the durable <c>PendingAskUserJson</c> payload
    /// (a serialized <c>AskUserRequest</c>) persisted on the conversation row, so a reloaded, newly-opened,
    /// or mobile client that missed the live <c>UserInputRequired</c> event can hydrate the inline prompt
    /// on connect (ask_user durability, #1488). The persisted shape uses the same camelCase field names as
    /// the live <see cref="AskUserRequestPayload"/>, with the input type serialized as a string and the
    /// timeout as an ISO duration; the extra session/agent fields are ignored. Returns false when the JSON
    /// is missing, malformed, or lacks the required request id / prompt / input type.
    /// </summary>
    /// <param name="json">Raw persisted <c>AskUserRequest</c> JSON, or null/empty when no prompt is pending.</param>
    /// <param name="conversationId">The conversation being hydrated, used when the payload omits its own id.</param>
    /// <param name="prompt">The reconstructed prompt state on success.</param>
    public static bool TryBuildFromPersistedJson(
        string? json,
        string conversationId,
        [NotNullWhen(true)] out AskUserPromptState? prompt)
    {
        prompt = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        AskUserRequestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AskUserRequestPayload>(json, PersistedAskUserJsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null)
            return false;

        var requestId = payload.RequestId;
        var promptText = payload.Prompt;
        var inputType = payload.InputType;
        if (string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(promptText) ||
            string.IsNullOrWhiteSpace(inputType))
        {
            return false;
        }

        // Prefer the conversation id carried in the payload, falling back to the conversation being
        // hydrated so the prompt always binds to the tab the user is looking at.
        var resolvedConversationId = string.IsNullOrWhiteSpace(payload.ConversationId)
            ? conversationId
            : payload.ConversationId!;

        prompt = new AskUserPromptState
        {
            RequestId = requestId!,
            ConversationId = resolvedConversationId,
            Prompt = promptText!,
            InputType = inputType!,
            Choices = ParseChoices(metadata: null, payload.Choices),
            AllowMultiple = payload.AllowMultiple,
            AllowFreeForm = payload.AllowFreeForm,
            ExpiresAt = ParseExpiration(payload.Timeout)
        };

        return true;
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

    private static IReadOnlyList<AskUserChoiceState>? ParseChoices(
        IReadOnlyDictionary<string, JsonElement>? metadata,
        IReadOnlyList<AskUserChoicePayload>? fallbackChoices)
    {
        if (metadata is not null && metadata.TryGetValue("choices", out var rawChoices))
        {
            var parsed = ParseChoicesFromJson(rawChoices);
            if (parsed is { Count: > 0 })
                return parsed;
        }

        if (fallbackChoices is null || fallbackChoices.Count == 0)
            return null;

        return fallbackChoices
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Value))
            .Select(choice => new AskUserChoiceState(
                choice.Value!,
                string.IsNullOrWhiteSpace(choice.Label) ? choice.Value! : choice.Label!,
                choice.Description))
            .ToList();
    }

    private static IReadOnlyList<AskUserChoiceState>? ParseChoicesFromJson(JsonElement rawChoices)
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

        var choices = new List<AskUserChoiceState>();
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

            choices.Add(new AskUserChoiceState(
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
}
