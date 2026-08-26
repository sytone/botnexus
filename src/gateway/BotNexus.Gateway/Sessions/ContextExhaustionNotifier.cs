using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Decides whether a run that ended on an empty assistant completion did so because the provider
/// context window was exhausted, and if so records the user-visible notice (#3535).
/// </summary>
/// <remarks>
/// This is a DECISION + TRANSCRIPT seam, not a delivery seam. Live delivery is the caller's job:
/// <c>StreamingSessionHelper</c> emits the returned text through the same stream-event callback the
/// rest of the run's content already flows through, so the notice reaches every channel by the path
/// that channel already renders. Doing it that way also keeps this type clear of the
/// <c>ChannelKnowledgeFence</c> rule 5 ban on direct <c>IChannelAdapter</c> sends from generic
/// orchestration code - the fence baselines <see cref="InterruptedTurnNotificationService"/>'s direct
/// send as debt to be removed, so copying that shape would have added to a list contracted to shrink.
/// </remarks>
public interface IContextExhaustionNotifier
{
    /// <summary>
    /// Decides whether the supplied session's contentless completion is explained by an exhausted
    /// context window and, if so, appends the <see cref="MessageRole.Notification"/> transcript row.
    /// </summary>
    /// <param name="session">The session whose run terminated on an empty assistant completion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The notice text when the window really was exhausted (the caller delivers it), or
    /// <see langword="null"/> when this completion is not exhaustion and must stay silent.
    /// </returns>
    Task<string?> TryBuildNoticeAsync(GatewaySession session, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IContextExhaustionNotifier"/>.
/// </summary>
/// <remarks>
/// <para>
/// The window is resolved through <see cref="ISessionContextWindowResolver"/> (#2896) rather than
/// read from the global <c>CompactionOptions.ContextWindowTokens</c>, so an agent or conversation
/// carrying a scoped override is judged against the window it is actually running under. That is
/// AC4 of #3535 and it is not cosmetic: a conversation pinned to 32k exhausts at 32k regardless of
/// what the process-global setting says.
/// </para>
/// <para>
/// The transcript row uses <see cref="MessageRole.Notification"/>, which
/// <c>SessionContextProjector.IsVisibleInLiveContext</c> excludes - so the notice cannot itself
/// consume the very window it is reporting on, nor be swept into a later compaction summary.
/// </para>
/// <para>
/// Every step is best-effort. This runs at the tail of a run that has ALREADY failed to answer;
/// throwing here would replace a silent turn with a crashed one, which is worse.
/// </para>
/// </remarks>
public sealed class ContextExhaustionNotifier : IContextExhaustionNotifier
{
    private readonly ISessionContextWindowResolver? _windowResolver;
    private readonly ILogger<ContextExhaustionNotifier> _logger;

    public ContextExhaustionNotifier(
        ILogger<ContextExhaustionNotifier> logger,
        ISessionContextWindowResolver? windowResolver = null)
    {
        _logger = logger;
        _windowResolver = windowResolver;
    }

    /// <inheritdoc />
    public async Task<string?> TryBuildNoticeAsync(GatewaySession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var promptTokens = ContextExhaustionNotice.ReadProviderPromptTokens(session);
        if (promptTokens is not > 0)
        {
            // No provider ever reported a prompt cost for this session, so there is no evidence the
            // window was exhausted. Absence is "unavailable", never zero - stay silent.
            return null;
        }

        var window = await ResolveWindowAsync(session, cancellationToken).ConfigureAwait(false);
        if (!ContextExhaustionNotice.IsExhausted(promptTokens, window))
        {
            return null;
        }

        var content = ContextExhaustionNotice.BuildMessage(promptTokens!.Value, window!.Value);

        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Notification,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow
        });

        _logger.LogWarning(
            "Session '{SessionId}' terminated on an empty assistant completion with {PromptTokens} prompt tokens " +
            "against a resolved {Window}-token context window; notifying the user that the window is exhausted (#3535).",
            session.SessionId.Value,
            promptTokens.Value,
            window.Value);

        return content;
    }

    private async Task<int?> ResolveWindowAsync(GatewaySession session, CancellationToken cancellationToken)
    {
        if (_windowResolver is null)
        {
            return null;
        }

        try
        {
            return await _windowResolver
                .ResolveAsync(session.AgentId, session.ConversationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve the context window for session '{SessionId}'.", session.SessionId.Value);
            return null;
        }
    }
}
