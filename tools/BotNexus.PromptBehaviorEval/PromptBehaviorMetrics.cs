namespace BotNexus.PromptBehaviorEval;

/// <summary>One observable assistant narration or completed tool call in run order.</summary>
public sealed record BehaviorObservation(string Kind, string Name, string? Detail, bool Accepted = true)
{
    /// <summary>Creates an assistant-message observation.</summary>
    public static BehaviorObservation Assistant(string text) => new("assistant", "assistant", text);

    /// <summary>Creates a tool-call observation, optionally carrying a todo status transition.</summary>
    public static BehaviorObservation Tool(string name, string? transition = null, bool accepted = true) => new("tool", name, transition, accepted);
}

/// <summary>Machine-readable behavioral measurements extracted from an actual agent loop run.</summary>
public sealed record PromptBehaviorMetrics(
    IReadOnlyList<string> ToolOrder,
    IReadOnlyList<string> TodoTransitions,
    int AssistantMessageCount,
    int MaximumSilentToolCallSpacing,
    int RejectedOperationCount)
{
    /// <summary>Computes ordering and narration-spacing metrics from ordered observations.</summary>
    public static PromptBehaviorMetrics Compute(IEnumerable<BehaviorObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var tools = new List<string>();
        var transitions = new List<string>();
        var assistantCount = 0;
        var currentSilentTools = 0;
        var maximumSilentTools = 0;
        var rejectedOperations = 0;

        foreach (var observation in observations)
        {
            if (string.Equals(observation.Kind, "assistant", StringComparison.Ordinal))
            {
                assistantCount++;
                if (!string.IsNullOrWhiteSpace(observation.Detail))
                {
                    maximumSilentTools = Math.Max(maximumSilentTools, currentSilentTools);
                    currentSilentTools = 0;
                }
                continue;
            }
            if (!string.Equals(observation.Kind, "tool", StringComparison.Ordinal))
                continue;

            tools.Add(observation.Name);
            currentSilentTools++;
            if (!observation.Accepted)
                rejectedOperations++;
            if (string.Equals(observation.Name, "todo", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(observation.Detail))
            {
                transitions.Add(observation.Detail);
            }
        }

        maximumSilentTools = Math.Max(maximumSilentTools, currentSilentTools);
        return new PromptBehaviorMetrics(tools, transitions, assistantCount, maximumSilentTools, rejectedOperations);
    }
}
