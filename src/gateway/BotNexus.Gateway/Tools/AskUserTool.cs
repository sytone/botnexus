using System.Globalization;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Allows an agent to pause mid-turn and request structured user input while preserving
/// the active tool-call context.
/// </summary>
public sealed class AskUserTool(
    IAskUserResponseRegistry responseRegistry,
    AgentId agentId,
    SessionId sessionId,
    ConversationId? conversationId,
    IConversationStore? conversationStore = null) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Name => "ask_user";
    public string Label => "Ask User";

    public Tool Definition => new(
        Name,
        "Pause execution and request user input before continuing.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "prompt": {
                  "type": "string",
                  "description": "Question to present to the user."
                },
                "input_type": {
                  "type": "string",
                  "enum": ["free_form", "single_choice", "multiple_choice", "choice_or_free_form"],
                  "description": "Input mode for the question."
                },
                "choices": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "value": { "type": "string" },
                      "label": { "type": "string" },
                      "description": { "type": "string" }
                    },
                    "required": ["value"]
                  }
                },
                "allow_multiple": {
                  "type": "boolean",
                  "description": "Allow selecting more than one choice."
                },
                "timeout_seconds": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": 3600,
                  "description": "Seconds to wait before timing out (default 300)."
                }
              },
              "required": ["prompt"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = ReadRequiredString(arguments, "prompt");
        _ = ReadInputType(arguments);
        _ = ReadChoices(arguments);
        _ = ReadBool(arguments, "allow_multiple");
        _ = ReadInt(arguments, "timeout_seconds");
        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        if (conversationId is null)
            throw new InvalidOperationException("ask_user requires an active conversation context.");

        var prompt = ReadRequiredString(arguments, "prompt");
        var inputType = ReadInputType(arguments);
        var choices = ReadChoices(arguments);
        var timeoutSeconds = Math.Clamp(ReadInt(arguments, "timeout_seconds") ?? 300, 1, 3600);
        var allowMultiple = ReadBool(arguments, "allow_multiple") ?? inputType == AskUserInputType.MultipleChoice;
        var allowFreeForm = inputType is AskUserInputType.FreeForm or AskUserInputType.ChoiceOrFreeForm;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var registration = responseRegistry.Register(conversationId.Value, timeout);

        var request = new AskUserRequest
        {
            RequestId = registration.RequestId,
            ConversationId = conversationId.Value,
            SessionId = sessionId,
            AgentId = agentId,
            Prompt = prompt,
            InputType = inputType,
            Choices = choices,
            AllowMultiple = allowMultiple,
            AllowFreeForm = allowFreeForm,
            Timeout = timeout
        };

        try
        {
            // The registry entry above is live pending-input state. Any failure between here and
            // the wait resolving (e.g. the widget-emit callback throwing) must cancel that entry,
            // otherwise the conversation stays permanently pending and silently swallows every
            // subsequent user message (#1916). The broad try/finally below already clears the
            // durable copy on every exit path; extend it to own the registration lifetime too.
            onUpdate?.Invoke(new AgentToolResult(Array.Empty<AgentToolContent>(), request));

            // Persist the pending prompt as durable conversation-scoped state so a reloaded tab, a
            // newly-opened window, mobile that missed the live UserInputRequired event, or a gateway
            // restart can rehydrate it (ask_user durability, #1488). Best-effort: a persistence hiccup
            // must never break the interactive prompt itself.
            await PersistPendingPromptAsync(request, cancellationToken).ConfigureAwait(false);

            var response = await registration.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return TextResult(JsonSerializer.Serialize(response, JsonOptions));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            responseRegistry.Cancel(registration.RequestId);
            throw;
        }
        catch (OperationCanceledException)
        {
            return TextResult(JsonSerializer.Serialize(new AskUserResponse
            {
                RequestId = registration.RequestId,
                WasCancelled = true
            }, JsonOptions));
        }
        catch
        {
            // Any other failure after Register (widget emit, persistence rethrow, etc.) must not
            // leave the registry entry live -- self-heal by cancelling so the session unblocks
            // instead of soft-locking in pending-input state (#1916, criterion 4). Re-throw so the
            // agent loop still receives the error.
            responseRegistry.Cancel(registration.RequestId);
            throw;
        }
        finally
        {
            // The wait has resolved by every path (answer, timeout, cancel, or exception) -- the
            // prompt is no longer pending, so clear the durable copy. Using CancellationToken.None
            // because the caller's token may already be cancelled and the clear must still run.
            await ClearPendingPromptAsync(conversationId.Value, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PersistPendingPromptAsync(AskUserRequest request, CancellationToken cancellationToken)
    {
        if (conversationStore is null)
            return;

        try
        {
            var conversation = await conversationStore.GetAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
            if (conversation is null)
                return;

            var pendingJson = JsonSerializer.Serialize(request, JsonOptions);

            // SaveAsync is compare-and-swap guarded (#2471); a pin or canvas write landing between the
            // read above and this save throws instead of clobbering. Retry through the shared helper,
            // which re-reads and re-applies ONLY the pending-prompt field to the fresh aggregate (#2131).
            _ = await ConversationSaveRetry.SaveWithRetryAsync(
                conversationStore,
                request.ConversationId,
                conversation,
                source => source with { PendingAskUserJson = pendingJson },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Durability is a best-effort enhancement; never fail the prompt because persistence hiccuped.
        }
    }

    private async Task ClearPendingPromptAsync(ConversationId conversation, CancellationToken cancellationToken)
    {
        if (conversationStore is null)
            return;

        try
        {
            var stored = await conversationStore.GetAsync(conversation, cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.PendingAskUserJson is null)
                return;

            // Same CAS concern as the register path: re-apply only the clear to fresh state so a
            // concurrent writer's columns survive the retry (#2131).
            _ = await ConversationSaveRetry.SaveWithRetryAsync(
                conversationStore,
                conversation,
                stored,
                source => source.PendingAskUserJson is null ? null : source with { PendingAskUserJson = null },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort clear; a stale pending row is reconciled on the next register/clear cycle.
        }
    }

    private static string ReadRequiredString(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        var value = ReadString(arguments, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Missing required argument: {key}.");

        return value.Trim();
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } element when bool.TryParse(element.GetString(), out var parsed) => parsed,
            bool parsed => parsed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => throw new ArgumentException($"Argument '{key}' must be a boolean.")
        };
    }

    /// <summary>
    /// Reads an optional <see cref="int"/> tool argument regardless of how the provider boxed the
    /// underlying JSON number. Streaming tool-call parsing boxes JSON integers as CLR
    /// <see cref="long"/> and any number carrying a decimal point as <see cref="double"/>, so the
    /// previous switch - which handled only <see cref="JsonElement"/>/<see cref="int"/>/
    /// <see cref="long"/>/<see cref="string"/> - rejected a schema-valid <c>timeout_seconds</c> with
    /// a message asserting the very requirement the payload already met (issue #2415). It also cast
    /// <see cref="long"/> to <see cref="int"/> unchecked, silently turning an out-of-range value into
    /// a plausible one.
    /// <para>
    /// This mirrors <c>AgentConverseTool.TryReadInt32</c>: a value is accepted only when it
    /// round-trips to <see cref="int"/> without loss. The duplication is deliberate - the two tools
    /// live in assemblies with no shared dependency, and introducing one to host a helper is a
    /// dependency decision that does not belong in a bug fix.
    /// </para>
    /// </summary>
    private static int? ReadInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (TryReadInt32(value, out var parsed))
            return parsed;

        throw new ArgumentException(
            $"Argument '{key}' must be a whole number that fits in a 32-bit integer. " +
            $"Received {DescribeValue(value)}; expected an integer such as 300 (a JSON number, or a " +
            "string containing only digits). Fractional, non-finite and out-of-range values are rejected " +
            "because they cannot be represented without loss.");
    }

    private static bool TryReadInt32(object value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case sbyte or byte or short or ushort:
                result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                result = (int)l;
                return true;
            case uint u when u <= int.MaxValue:
                result = (int)u;
                return true;
            case ulong u when u <= int.MaxValue:
                result = (int)u;
                return true;
            case double d when IsIntegralInt32(d):
                result = (int)d;
                return true;
            case float f when IsIntegralInt32(f):
                result = (int)f;
                return true;
            case decimal m when IsIntegralInt32(m):
                result = (int)m;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                return TryReadJsonNumber(element, out result);
            case JsonElement { ValueKind: JsonValueKind.String } element:
                return TryParseInt32(element.GetString(), out result);
            case string text:
                return TryParseInt32(text, out result);
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryReadJsonNumber(JsonElement element, out int result)
    {
        if (element.TryGetInt32(out result))
            return true;

        if (element.TryGetInt64(out var l) && l is >= int.MinValue and <= int.MaxValue)
        {
            result = (int)l;
            return true;
        }

        if (element.TryGetDouble(out var d) && IsIntegralInt32(d))
        {
            result = (int)d;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool IsIntegralInt32(double value)
        => double.IsFinite(value)
           && value % 1d == 0d
           && value is >= int.MinValue and <= int.MaxValue;

    private static bool IsIntegralInt32(decimal value)
        => value % 1m == 0m
           && value is >= int.MinValue and <= int.MaxValue;

    private static bool TryParseInt32(string? text, out int result)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Renders the value the caller actually sent so a rejection diagnoses rather than merely
    /// asserts. #2415's core complaint was messages that restated a requirement without saying what
    /// was received, leaving the model to retry blindly.
    /// </summary>
    private static string DescribeValue(object value)
    {
        if (value is JsonElement element)
        {
            var rendered = element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? element.ValueKind.ToString().ToLowerInvariant()
                : element.ToString();
            return $"JSON {element.ValueKind.ToString().ToLowerInvariant()} '{rendered}'";
        }

        var text = value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString();

        return $"{value.GetType().Name} '{text}'";
    }

    private static AskUserInputType ReadInputType(IReadOnlyDictionary<string, object?> arguments)
    {
        var value = ReadString(arguments, "input_type");
        if (string.IsNullOrWhiteSpace(value))
            return AskUserInputType.FreeForm;

        return value.Trim().ToLowerInvariant() switch
        {
            "free_form" => AskUserInputType.FreeForm,
            "single_choice" => AskUserInputType.SingleChoice,
            "multiple_choice" => AskUserInputType.MultipleChoice,
            "choice_or_free_form" => AskUserInputType.ChoiceOrFreeForm,
            _ => throw new ArgumentException($"Unsupported input_type '{value}'.")
        };
    }

    private static IReadOnlyList<AskUserChoice>? ReadChoices(IReadOnlyDictionary<string, object?> args)
    {
        if (!args.TryGetValue("choices", out var value) || value is null)
            return null;

        if (value is IEnumerable<object?> enumerable)
        {
            var json = JsonSerializer.Serialize(enumerable);
            using var document = JsonDocument.Parse(json);
            return ReadChoicesFromJsonArray(document.RootElement);
        }

        if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            return ReadChoicesFromJsonArray(array);
        }

        throw new ArgumentException("choices must be an array.");
    }

    private static IReadOnlyList<AskUserChoice> ReadChoicesFromJsonArray(JsonElement array)
    {
        List<AskUserChoice> choices = [];
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("choices must contain objects.");

            var optionValue = item.TryGetProperty("value", out var valueElement)
                ? valueElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(optionValue))
                throw new ArgumentException("Each choice requires a non-empty value.");

            var label = item.TryGetProperty("label", out var labelElement) ? labelElement.GetString() : null;
            var description = item.TryGetProperty("description", out var descElement) ? descElement.GetString() : null;
            choices.Add(new AskUserChoice
            {
                Value = optionValue,
                Label = label,
                Description = description
            });
        }

        return choices;
    }

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);
}
