using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Handle to a running agent instance within an isolation boundary.
/// Provides the interaction surface for sending prompts and receiving responses.
/// </summary>
/// <remarks>
/// <para>
/// Each handle wraps the underlying execution environment (in-process, sandbox,
/// container, remote). The Gateway interacts with agents exclusively through this
/// interface, making the isolation strategy transparent to routing and API layers.
/// </para>
/// <para>
/// For in-process isolation, this wraps a <c>BotNexus.AgentCore.Agent</c> directly.
/// For container or remote isolation, this would be a gRPC/HTTP proxy.
/// </para>
/// </remarks>
public interface IAgentHandle : IAsyncDisposable
{
    /// <summary>The agent ID this handle is for.</summary>
    string AgentId { get; }

    /// <summary>The session ID this handle is bound to.</summary>
    string SessionId { get; }

    /// <summary>Whether the agent is currently processing a request.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Sends a message to the agent and waits for the complete response.
    /// Use <see cref="StreamAsync"/> for real-time streaming.
    /// </summary>
    /// <param name="message">The user message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete agent response.</returns>
    Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the agent and streams back events in real time.
    /// Events include content deltas, tool execution updates, and completion.
    /// </summary>
    /// <param name="message">The user message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of agent events.</returns>
    IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts the current agent execution, if any.
    /// </summary>
    Task AbortAsync(CancellationToken cancellationToken = default);
}
