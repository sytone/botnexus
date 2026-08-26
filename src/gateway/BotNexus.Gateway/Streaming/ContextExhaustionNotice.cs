using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Streaming;

/// <summary>
/// #3535: the discrimination policy and wording for the ONE contentless-completion shape that is a
/// user-facing failure rather than normal model behaviour - a run that ended empty because the
/// provider context window was exhausted.
/// </summary>
/// <remarks>
/// <para>
/// Three shapes reach the <c>#2921</c>/<c>#3129</c> branch in
/// <see cref="StreamingSessionHelper"/> and only one of them is a defect the user needs told about:
/// </para>
/// <list type="bullet">
/// <item>a thinking-only completion (<c>#1198</c>) - normal, must stay silent;</item>
/// <item>a normal <c>ContentDelta -&gt; MessageEnd -&gt; TurnEnd</c> run whose per-turn buffers were
/// already flushed (<c>#3129</c>) - normal, must stay silent;</item>
/// <item>a run whose prompt already fills the context window, so the provider had no room to answer -
/// this one, and only this one, gets a message.</item>
/// </list>
/// <para>
/// The discriminator is deliberately arithmetic and evidence-based rather than heuristic: it compares
/// the provider's OWN reported prompt-token count for the last completed request
/// (<c>lastProviderPromptTokens</c>, written by <see cref="ProviderTokenUsageRecorder"/>) against the
/// SCOPE-RESOLVED context window from <c>ISessionContextWindowResolver</c> (#2896). Using the raw
/// global <c>CompactionOptions.ContextWindowTokens</c> here would be wrong in the dangerous direction:
/// an agent or conversation pinned to a smaller window exhausts long before the global figure, and
/// one pinned to a larger one would be told it was exhausted when it was not.
/// </para>
/// <para>
/// Both inputs are opportunistic. A session with no recorded provider count, or no resolvable window,
/// yields <see langword="false"/> and keeps the pre-#3535 silent behaviour - absence is
/// "unavailable", never zero.
/// </para>
/// </remarks>
public static class ContextExhaustionNotice
{
    /// <summary>
    /// Fraction of the resolved window within which a prompt is treated as having exhausted it.
    /// A provider does not have to reach the window exactly to have no usable room left: the reply
    /// itself needs budget, and framing overhead is not visible to us. 5% of the window is the
    /// margin the issue specifies.
    /// </summary>
    public const double ExhaustionMargin = 0.05;

    /// <summary>
    /// The user-visible notice. Names the cause (the conversation filled its context window) and two
    /// concrete remedies, per AC2.
    /// </summary>
    internal const string NoticeTemplate =
        "⚠️ This conversation has run out of context room, so the model had no space left to reply " +
        "(about {0:N0} of {1:N0} tokens are already in use). Nothing was lost - but to keep going, " +
        "run /compact to summarise the history, or start a new conversation.";

    /// <summary>
    /// Decides whether a contentless completion is explained by an exhausted context window.
    /// </summary>
    /// <param name="providerPromptTokens">
    /// The provider's reported prompt-token count for the last completed request, or
    /// <see langword="null"/> when no provider has reported one.
    /// </param>
    /// <param name="resolvedContextWindowTokens">
    /// The scope-resolved context window in tokens (conversation override, then agent, then model),
    /// or <see langword="null"/> when no layer supplies one.
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when both values are usable AND the prompt is within
    /// <see cref="ExhaustionMargin"/> of the window.
    /// </returns>
    public static bool IsExhausted(int? providerPromptTokens, int? resolvedContextWindowTokens)
    {
        if (providerPromptTokens is not > 0 || resolvedContextWindowTokens is not > 0)
        {
            return false;
        }

        var window = resolvedContextWindowTokens.Value;
        var threshold = window * (1.0 - ExhaustionMargin);
        return providerPromptTokens.Value >= threshold;
    }

    /// <summary>Builds the user-visible notice for a given prompt count and resolved window.</summary>
    public static string BuildMessage(int providerPromptTokens, int resolvedContextWindowTokens)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            NoticeTemplate,
            providerPromptTokens,
            resolvedContextWindowTokens);

    /// <summary>
    /// Reads the provider prompt-token count recorded on the session by
    /// <see cref="ProviderTokenUsageRecorder"/>, returning <see langword="null"/> when the key is
    /// absent or does not carry a usable positive count.
    /// </summary>
    /// <param name="session">The session to read; must not be null.</param>
    /// <returns>The recorded prompt tokens, or <see langword="null"/>.</returns>
    public static int? ReadProviderPromptTokens(GatewaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Metadata is null ||
            !session.Metadata.TryGetValue(LlmSessionCompactor.ProviderPromptTokensMetadataKey, out var raw) ||
            raw is null)
        {
            return null;
        }

        return raw switch
        {
            int i when i > 0 => i,
            long l when l > 0 && l <= int.MaxValue => (int)l,
            string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0 => parsed,
            _ => null
        };
    }
}
