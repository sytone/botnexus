using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Ralph;

/// <summary>
/// Starts one iteration of a ralph loop: a <em>fresh</em> session inside the existing conversation,
/// seeded with the conversation's current instructions as the turn prompt (issue #2818).
/// </summary>
public interface IRalphIterationRunner
{
    /// <summary>
    /// Runs a single iteration and reports whether the turn succeeded. A <c>false</c> result feeds the
    /// consecutive-failure circuit breaker; it never decides on its own whether to re-trigger.
    /// </summary>
    /// <param name="conversation">The ralph conversation to iterate.</param>
    /// <param name="prompt">The seed prompt - the conversation's instructions, read at trigger time.</param>
    /// <param name="iteration">The 1-based ordinal of this iteration, used to mint the session id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> RunIterationAsync(
        Conversation conversation,
        string prompt,
        int iteration,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IRalphIterationRunner"/>: mints a brand-new session bound to the same
/// conversation and prompts the agent with the conversation instructions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fresh session, not a fresh turn.</b> The session id is newly minted every iteration and the
/// session is created through <c>GetOrCreateAsync</c> on that new id, so it starts with an empty
/// history. That is what makes acceptance criterion 3 hold structurally: a fact stated only in
/// iteration N's transcript cannot reach iteration N+1, because iteration N+1's session was never
/// given iteration N's entries. Appending to one growing session would make the loop's behaviour a
/// function of accumulated context, so it would drift as it runs and eventually compact - which is
/// precisely how loop state gets silently lost.
/// </para>
/// <para>
/// <b>Instructions are re-read per iteration.</b> The prompt is passed in by the trigger, which reads
/// the conversation fresh from the store each time, so editing the instructions between iterations
/// changes the next iteration's prompt without recreating the conversation (criterion 4).
/// </para>
/// </remarks>
public sealed class RalphIterationRunner(
    IAgentSupervisor supervisor,
    ISessionStore sessions,
    IConversationStore conversations,
    ILogger<RalphIterationRunner> logger) : IRalphIterationRunner
{
    /// <summary>The session-id prefix that marks a session as a ralph loop iteration.</summary>
    public const string SessionIdPrefix = "ralph";

    /// <inheritdoc />
    public async Task<bool> RunIterationAsync(
        Conversation conversation,
        string prompt,
        int iteration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var sessionId = SessionId.From(
            $"{SessionIdPrefix}:{iteration}:{Guid.NewGuid():N}");

        var session = await sessions.GetOrCreateAsync(sessionId, conversation.AgentId, cancellationToken)
            .ConfigureAwait(false);
        session.ChannelType ??= ChannelKey.From(SessionIdPrefix);
        session.CallerId ??= $"{SessionIdPrefix}:{conversation.AgentId.Value}";
        session.SessionType = SessionType.UserAgent;
        session.ConversationId = conversation.ConversationId;
        session.Metadata["ralphIteration"] = iteration;
        await sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        conversation.ActiveSessionId = sessionId;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await conversations.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);

        try
        {
            var handle = await supervisor.GetOrCreateAsync(conversation.AgentId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            var response = await handle.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);

            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = prompt });
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
            await sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Ralph iteration {Iteration} for conversation '{ConversationId}' failed.",
                iteration,
                conversation.ConversationId);
            return false;
        }
    }
}
