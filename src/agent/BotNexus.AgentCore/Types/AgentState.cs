using BotNexus.AgentCore.Tools;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Types;

/// <summary>
/// Represents mutable runtime state for a pi-mono style agent session.
/// </summary>
public class AgentState
{
    private List<IAgentTool> _tools = [];
    private List<AgentMessage> _messages = [];
    private readonly HashSet<string> _pendingToolCalls = [];

    /// <summary>
    /// Gets or sets the effective system prompt.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Gets or sets the active model definition.
    /// </summary>
    public required LlmModel Model { get; set; }

    /// <summary>
    /// Gets or sets the thinking level used for model calls.
    /// </summary>
    public ThinkingLevel? ThinkingLevel { get; set; } = null;

    /// <summary>
    /// Gets or sets the registered tools.
    /// Setter copies incoming collections.
    /// </summary>
    public IReadOnlyList<IAgentTool> Tools
    {
        get => _tools;
        set => _tools = value?.ToList() ?? [];
    }

    /// <summary>
    /// Gets or sets the message timeline.
    /// Setter copies incoming collections.
    /// </summary>
    public IReadOnlyList<AgentMessage> Messages
    {
        get => _messages;
        set => _messages = value?.ToList() ?? [];
    }

    /// <summary>
    /// Gets a value indicating whether streaming is currently active.
    /// </summary>
    public bool IsStreaming => StreamingMessage is not null;

    /// <summary>
    /// Gets the current in-progress streaming assistant message.
    /// </summary>
    public AssistantAgentMessage? StreamingMessage { get; private set; }

    /// <summary>
    /// Gets the set of pending tool call identifiers.
    /// </summary>
    public IReadOnlySet<string> PendingToolCalls => _pendingToolCalls;

    /// <summary>
    /// Gets the latest runtime error message.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Sets the current streaming message.
    /// </summary>
    /// <param name="streamingMessage">The current streaming message or <see langword="null"/>.</param>
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
    /// <param name="errorMessage">The latest error message.</param>
    public void SetErrorMessage(string? errorMessage) => ErrorMessage = errorMessage;
}
