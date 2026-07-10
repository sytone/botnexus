using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

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
/// For in-process isolation, this wraps a <c>BotNexus.Agent.Core.Agent</c> directly.
/// For container or remote isolation, this would be a gRPC/HTTP proxy.
/// </para>
/// </remarks>
public interface IAgentHandle : IAsyncDisposable
{
    /// <summary>The agent ID this handle is for.</summary>
    AgentId AgentId { get; }

    /// <summary>The session ID this handle is bound to.</summary>
    SessionId SessionId { get; }

    /// <summary>Whether the agent is currently processing a request.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Sends a message to the agent and waits for the complete response.
    /// Use <see cref="StreamAsync(string,CancellationToken)"/> for real-time streaming.
    /// </summary>
    /// <param name="message">The user message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete agent response.</returns>
    Task<AgentResponse> PromptAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a multimodal user message (text + optional images) and waits for the complete response.
    /// Use the string overload when no images are present.
    /// </summary>
    /// <param name="message">The user message, optionally carrying image content parts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete agent response.</returns>
    Task<AgentResponse> PromptAsync(AgentUserMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the agent and streams back events in real time.
    /// Events include content deltas, tool execution updates, and completion.
    /// </summary>
    /// <param name="message">The user message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of agent events.</returns>
    IAsyncEnumerable<AgentStreamEvent> StreamAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a multimodal user message (text + optional images) and streams back events in real time.
    /// Use the string overload when no images are present.
    /// </summary>
    /// <param name="message">The user message, optionally carrying image content parts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of agent events.</returns>
    IAsyncEnumerable<AgentStreamEvent> StreamAsync(AgentUserMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts the current agent execution, if any.
    /// </summary>
    Task AbortAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Steers the running agent by injecting a message during execution.
    /// The message is queued and delivered between tool calls.
    /// </summary>
    /// <param name="message">The steering message to inject.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the message is queued.</returns>
    /// <remarks>Only effective while the agent is actively running (processing a prompt).</remarks>
    Task SteerAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Steers the running agent with a system-injected side turn (#1845) that must only be
    /// consumed at a genuine idle turn boundary. Used by the pre-compaction memory flush so a
    /// mid-flight flush turn cannot consume the loop's continuation and abandon the original
    /// in-flight task. When the agent is idle at inject time, behaves exactly like
    /// <see cref="SteerAsync(string, CancellationToken)"/>.
    /// </summary>
    /// <param name="message">The steering message to inject.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the message is queued.</returns>
    Task SteerDeferrableAsync(string message, CancellationToken cancellationToken = default)
        => SteerAsync(message, cancellationToken);

    /// <summary>
    /// Queues a follow-up message to be processed after the current agent run completes.
    /// </summary>
    /// <param name="message">The follow-up message to queue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the message is queued.</returns>
    Task FollowUpAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a typed follow-up message to be processed after the current agent run completes.
    /// </summary>
    /// <param name="message">The follow-up message to queue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the message is queued.</returns>
    Task FollowUpAsync(AgentMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically aborts the current agent run (if any) and injects a new steering direction.
    /// The new direction is queued immediately after abort completes so the agent resumes
    /// with the redirected goal rather than continuing the abandoned turn.
    /// </summary>
    /// <param name="message">The new direction to inject after aborting the current run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the abort is issued and the steer is queued.</returns>
    /// <remarks>
    /// This is the Phase 1a contract definition (Issue #799, Part of #704).
    /// Implementation is wired in Issue #800.
    /// </remarks>
    Task InterruptAndSteerAsync(string message, CancellationToken cancellationToken = default);
}
