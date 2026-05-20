using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
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
    ConversationId? conversationId) : IAgentTool
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

        onUpdate?.Invoke(new AgentToolResult(Array.Empty<AgentToolContent>(), request));

        try
        {
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

    private static int? ReadInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var number) => number,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            int number => number,
            long number => (int)number,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => throw new ArgumentException($"Argument '{key}' must be an integer.")
        };
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
