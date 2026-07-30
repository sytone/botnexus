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
    /// Steers the running agent with a fully-composed multimodal user message, so a steer issued
    /// from a composer that had draft attachments delivers those attachments rather than text only
    /// (#2484).
    /// </summary>
    /// <param name="message">The composed steering message, optionally carrying image content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the message is queued.</returns>
    /// <remarks>
    /// The default implementation preserves the composed text (which already carries inlined
    /// non-image attachments from <c>AgentUserMessageComposer</c>) but not the vision payload;
    /// handles that own a real steering queue MUST override it to inject the typed message intact.
    /// </remarks>
    Task SteerAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
        => SteerAsync(
            AgentHandleImageDropGuard.DegradeToText(this, message, AgentHandleImageDropGuard.SteerSite),
            cancellationToken);

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
    /// Queues <paramref name="message"/> as a follow-up <em>only</em> if a run is currently in
    /// flight, and reports whether it was queued.
    /// </summary>
    /// <param name="message">The follow-up text to queue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> when a run was in flight and the message is queued for delivery after it
    /// settles; <c>false</c> when the agent was idle (or became idle before the queued message
    /// could be claimed), in which case the message has NOT been queued and the caller must
    /// deliver it as an ordinary inbound message instead.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the primitive that makes hub-level follow-up correct (#2438). Doing the same thing
    /// as <c>if (handle.IsRunning) FollowUpAsync(...) else Send(...)</c> at the call site is racy:
    /// the run can settle between the check and the enqueue, leaving the message stranded in an
    /// idle agent's follow-up queue which is never drained again. Implementations must close that
    /// window - enqueue first, then re-verify the run is still live, and take the message back if
    /// it is not.
    /// </para>
    /// <para>
    /// Overflow of the bounded follow-up queue surfaces as an exception, never as a silent drop.
    /// </para>
    /// <para>
    /// The default implementation is the best a handle with no visibility into its own run
    /// lifecycle can do: it reports "not queued" so the caller falls back to a normal send. Any
    /// handle that really owns a follow-up queue MUST override this with the atomic
    /// enqueue-then-reverify form.
    /// </para>
    /// </remarks>
    Task<bool> TryFollowUpWhileRunningAsync(string message, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Typed counterpart of <see cref="TryFollowUpWhileRunningAsync(string, CancellationToken)"/>
    /// that carries a fully-composed multimodal user message so a follow-up issued with draft
    /// attachments survives the queue round-trip (#2484).
    /// </summary>
    /// <param name="message">The composed follow-up message, optionally carrying image content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> when a run was in flight and the message is queued; <c>false</c> when the agent
    /// was idle and the caller must deliver it as an ordinary inbound message instead.
    /// </returns>
    Task<bool> TryFollowUpWhileRunningAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

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

    /// <summary>
    /// Typed counterpart of <see cref="InterruptAndSteerAsync(string, CancellationToken)"/> that
    /// carries a fully-composed multimodal user message so a redirect issued with draft attachments
    /// delivers them rather than dropping them (#2484).
    /// </summary>
    /// <param name="message">The composed redirect message, optionally carrying image content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the abort is issued and the steer is queued.</returns>
    Task InterruptAndSteerAsync(AgentUserMessage message, CancellationToken cancellationToken = default)
        => InterruptAndSteerAsync(
            AgentHandleImageDropGuard.DegradeToText(this, message, AgentHandleImageDropGuard.RedirectSite),
            cancellationToken);
}
