using BotNexus.Domain.Primitives;

namespace BotNexus.Cron;

/// <summary>
/// THE single decision for "which conversation should a newly created cron job be bound to?"
/// (#2412 clause 1).
/// </summary>
/// <remarks>
/// <para>
/// Before this existed, <see cref="CronJob.ConversationId"/> was always <c>null</c> at create time
/// and was only stamped later, by the scheduler's CAS, from whatever conversation the FIRST RUN
/// happened to materialise. An agent that created a job mid-conversation therefore lost that
/// binding: the job's output landed in a freshly minted cron conversation the human was not
/// reading. The repo has already been bitten by the wider dangling-binding problem - see
/// <c>CronScheduler.MigrateLegacyCronConversationsAsync</c>, a one-shot startup migration that
/// exists purely to reconcile cron sessions orphaned by conversation bindings that were never
/// established up front.
/// </para>
/// <para>
/// <b>Durability is the whole point of the seam.</b> The bound value must be the DURABLE
/// conversation/session key - the id persisted on the conversation store and reachable after a
/// restart - and never a transient, policy-scoped, or channel-scoped key. A policy-scoped key can
/// be empty and can be cleanup-retired, which would leave the job pointing at a conversation that
/// no longer exists: exactly the dangling binding this is meant to prevent. That is why the input
/// is a <see cref="ConversationId"/> resolved from the conversation/session stores rather than any
/// per-request routing token, and why an uninitialised value is rejected rather than coerced.
/// </para>
/// <para>
/// The default is deliberately NARROW. It applies only to an <c>agent-prompt</c> job created by an
/// agent that genuinely has a resolved durable conversation. Every other caller keeps today's
/// behaviour verbatim:
/// </para>
/// <list type="bullet">
///   <item><description>a CLI or REST/API caller has no conversation context, so the job stays
///   unbound (<c>isolated</c>) and the scheduler's first-run CAS pins a fresh conversation exactly
///   as it does today;</description></item>
///   <item><description>a <c>command</c> job costs no model turn and produces no conversational
///   output, so binding it to a human's conversation would be noise;</description></item>
///   <item><description>heartbeat, memory-dreaming, skill-review and any other system-provisioned
///   job manages its own per-agent conversation and is left completely alone.</description></item>
/// </list>
/// </remarks>
public static class CronConversationDefault
{
    /// <summary>The one action type that produces conversational output worth binding.</summary>
    private const string AgentPromptActionType = "agent-prompt";

    /// <summary>
    /// Resolves the conversation a newly created job should be bound to, or <c>null</c> to leave the
    /// job unbound (the pre-#2412 behaviour, i.e. an <c>isolated</c> session whose conversation the
    /// scheduler pins on first run).
    /// </summary>
    /// <param name="actionType">The job's normalised action type.</param>
    /// <param name="isSystemJob">Whether the job is system-provisioned (heartbeat and friends).</param>
    /// <param name="explicitConversationId">A conversation the caller pinned deliberately. Always
    /// wins - a default must never override an explicit choice.</param>
    /// <param name="creatingConversationId">The DURABLE conversation the creating agent is speaking
    /// in, or <c>null</c> when the caller has no conversation context (CLI/API).</param>
    /// <returns>The conversation to persist on the new job, or <c>null</c> for unbound.</returns>
    public static ConversationId? Resolve(
        string? actionType,
        bool isSystemJob,
        ConversationId? explicitConversationId,
        ConversationId? creatingConversationId)
    {
        // An explicitly supplied binding is a caller decision, not a gap to be filled.
        if (explicitConversationId is { } explicitly && explicitly.IsInitialized())
            return explicitly;

        if (isSystemJob)
            return null;

        if (!string.Equals(actionType, AgentPromptActionType, StringComparison.OrdinalIgnoreCase))
            return null;

        // No conversation context (CLI, REST, a headless exec) => unbound, exactly as before.
        // An uninitialised ConversationId is the Vogen default sentinel and is NOT a durable key;
        // treating it as one would persist an unusable binding, which is strictly worse than none.
        if (creatingConversationId is not { } creating || !creating.IsInitialized())
            return null;

        return creating;
    }
}
