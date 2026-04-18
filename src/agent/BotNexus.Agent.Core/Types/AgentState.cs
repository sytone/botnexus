using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Types;

/// <summary>
/// Represents mutable runtime state for a pi-mono style agent session.
/// </summary>
/// <remarks>
/// AgentState is owned by Agent and accessed via the State property.
/// Mutations to Tools and Messages are visible immediately but do not affect in-flight runs.
/// Streaming and error state is managed internally via private setters.
/// </remarks>
public class AgentState
{
    private List<IAgentTool> _tools = [];
    private List<AgentMessage> _messages = [];
    private readonly HashSet<string> _pendingToolCalls = [];

    /// <summary>
    /// Gets or sets the effective system prompt.
    /// </summary>
    /// <remarks>
    /// Sent to the LLM at the start of each context. Set to null to omit.
    /// Changes take effect on the next PromptAsync/ContinueAsync call.
    /// </remarks>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Gets or sets the active model definition.
    /// </summary>
    /// <remarks>
    /// Specifies which LLM provider and model to use for generation.
    /// Changes take effect on the next PromptAsync/ContinueAsync call.
    /// </remarks>
    public required LlmModel Model { get; set; }

    /// <summary>
    /// Gets or sets the thinking level used for model calls.
    /// </summary>
    /// <remarks>
    /// Controls extended reasoning behavior for models that support thinking/reasoning modes.
    /// Set to null to use provider defaults.
    /// </remarks>
    public ThinkingLevel? ThinkingLevel { get; set; } = null;

    /// <summary>
    /// Gets or sets the registered tools.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setter copies the provided top-level collection to prevent external mutation.
    /// Tools are exposed to the model during generation and are available for execution.
    /// </para>
    /// <para>
    /// Changes take effect on the next PromptAsync/ContinueAsync call.
    /// Modifying tools mid-run does not affect the current run.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IAgentTool> Tools
    {
        get => _tools;
        set => _tools = value?.ToList() ?? [];
    }

    /// <summary>
    /// Gets or sets the message timeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setter copies the provided top-level collection to prevent external mutation.
    /// The timeline is the source of truth for the conversation history.
    /// </para>
    /// <para>
    /// New messages are appended during runs. Manually setting Messages replaces the entire timeline.
    /// Use with caution — losing history may confuse the model.
    /// </para>
    /// </remarks>
    public IReadOnlyList<AgentMessage> Messages
    {
        get => _messages;
        set => _messages = value?.ToList() ?? [];
    }

    /// <summary>
    /// Gets a value indicating whether an agent run is currently active.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Gets a value indicating whether streaming is currently active.
    /// </summary>
    /// <remarks>
    /// True between MessageStartEvent and MessageEndEvent during LLM generation.
    /// </remarks>
    public bool IsStreaming => StreamingMessage is not null;

    /// <summary>
    /// Gets the current in-progress streaming assistant message.
    /// </summary>
    /// <remarks>
    /// Non-null between MessageStartEvent and MessageEndEvent. Contains accumulated content and tool calls.
    /// Reset to null when streaming completes.
    /// </remarks>
    public AssistantAgentMessage? StreamingMessage { get; private set; }

    /// <summary>
    /// Gets the set of pending tool call identifiers.
    /// </summary>
    /// <remarks>
    /// Populated during tool execution (between ToolExecutionStartEvent and ToolExecutionEndEvent).
    /// Used to track which tools are currently running.
    /// </remarks>
    public IReadOnlySet<string> PendingToolCalls => _pendingToolCalls;

    /// <summary>
    /// Gets the aggregated last-error message for the agent state.
    /// </summary>
    /// <remarks>
    /// This is session-level error state, not a per-message field.
    /// It is set when a run fails and synchronized from assistant message errors at turn end.
    /// Cleared at the start of each new run.
    /// </remarks>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Sets whether an agent run is currently active.
    /// </summary>
    /// <param name="isRunning">True when a run is active.</param>
    public void SetIsRunning(bool isRunning) => IsRunning = isRunning;

    /// <summary>
    /// Sets the current streaming message.
    /// </summary>
    /// <param name="streamingMessage">The current streaming message or <see langword="null"/> to clear.</param>
    public void SetStreamingMessage(AssistantAgentMessage? streamingMessage) => StreamingMessage = streamingMessage;

    /// <summary>
    /// Replaces the set of pending tool calls.
    /// </summary>
    /// <param name="toolCallIds">The pending tool call identifiers.</param>
    public void SetPendingToolCalls(IEnumerable<string> toolCallIds)
    {
        _pendingToolCalls.Clear();
        foreach (var toolCallId in toolCallIds)
        {
            _pendingToolCalls.Add(toolCallId);
        }
    }

    /// <summary>
    /// Sets the latest runtime error message.
    /// </summary>
    /// <param name="errorMessage">The latest error message or <see langword="null"/> to clear.</param>
    public void SetErrorMessage(string? errorMessage) => ErrorMessage = errorMessage;
}
