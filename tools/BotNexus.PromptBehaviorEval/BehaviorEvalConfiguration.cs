using System.Text.Json.Serialization;

namespace BotNexus.PromptBehaviorEval;

/// <summary>Explicit provider, model, prompt, and output configuration for one paid evaluation run.</summary>
public sealed record BehaviorEvalConfiguration
{
    /// <summary>OpenAI-compatible endpoint base URL.</summary>
    public required string Endpoint { get; init; }
    /// <summary>Provider identity recorded in the result and sent with the model definition.</summary>
    public required string Provider { get; init; }
    /// <summary>Exact model identifier transmitted to the endpoint.</summary>
    public required string Model { get; init; }
    /// <summary>Environment variable containing the API key; the key is never serialized.</summary>
    public required string ApiKeyEnvironmentVariable { get; init; }
    /// <summary>Guidance rung to evaluate.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PromptGuidanceRung>))]
    public PromptGuidanceRung Rung { get; init; }
    /// <summary>Historical mutation flags to inject.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PromptMutation>))]
    public PromptMutation Mutation { get; init; }
    /// <summary>User task matching issue #3668's discovery and multi-item completion scenario.</summary>
    public string Task { get; init; } = "Start a checklist with at least two items, inspect the fixture, add any newly required work revealed by inspection, apply the requested change, then check configuration, validate the fixture schema, inspect the change diff, compile the harness and contract-test projects, review compile diagnostics, verify completion, complete at least two checklist items, and finish all work in this turn.";
    /// <summary>Provider output-token ceiling.</summary>
    public int MaxTokens { get; init; } = 2048;
    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 120;
    /// <summary>Path receiving indented JSON output.</summary>
    public required string OutputPath { get; init; }
}

/// <summary>Complete machine-readable result of one real-model behavior evaluation.</summary>
public sealed record BehaviorEvalResult(
    string Provider,
    string Model,
    PromptGuidanceRung Rung,
    PromptMutation Mutation,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    PromptBehaviorMetrics Metrics,
    IReadOnlyList<IReadOnlyList<TodoItem>> TodoSnapshots,
    IReadOnlyList<TodoTransition> TodoTransitions,
    bool AddedDiscoveredItemAfterInspection,
    int DistinctDoneItemCount,
    BehaviorAcceptance Acceptance,
    IReadOnlyList<BehaviorObservation> Observations,
    int InputTokens,
    int OutputTokens,
    string FinalAssistantMessage,
    string SystemPrompt);
