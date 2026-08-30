using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

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
///   <item><description>#3521: a conversation that is a HUMAN's thread rather than an agent- or
///   cron-owned one is never ADOPTED. See below - this is the narrowing the original default was
///   missing.</description></item>
/// </list>
/// <para>
/// <b>#3521 - adoption is not binding.</b> The #2412 default fills a gap; it must not confiscate a
/// thread that already belongs to somebody. A conversation whose <see cref="ConversationKind"/> is
/// <see cref="ConversationKind.HumanAgent"/>, or which is the agent's <see cref="Conversation.IsDefault"/>
/// home, is a human's long-running thread. Binding a job to it has two user-visible consequences for
/// the life of the job: the portal reclassifies the thread into the "Cron" bucket (its id is now in
/// the live job list), and <c>CronScheduler.DeleteJobAsync</c> treats it as the job's own property
/// and archives it on delete. In production this pointed a one-shot job at a 6,324-message human
/// conversation. Declining to bind costs nothing: the scheduler's first-run CAS mints a
/// <c>cronconv:</c> conversation, which is exactly what an unbound job has always done.
/// </para>
/// <para>
/// <b>The guard is deliberately not applied when provenance is UNKNOWN.</b> A null
/// <c>creatingConversationKind</c> means the caller could not read the conversation row at all (no
/// conversation store wired, or the row is gone). Treating "unknown" as "human" would silently
/// disable the #2412 binding for every caller that does not thread provenance through, which is a
/// regression of AC6 dressed up as caution. Unknown therefore preserves the pre-#3521 behaviour
/// verbatim, and the production path always supplies it.
/// </para>
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
    /// <param name="creatingConversationKind">The creating conversation's citizen-pairing
    /// discriminator (#3521), or <c>null</c> when the caller could not resolve the conversation row.
    /// <see cref="ConversationKind.HumanAgent"/> declines the binding.</param>
    /// <param name="creatingConversationIsDefault">Whether the creating conversation is the agent's
    /// default home thread (#3521). <c>true</c> declines the binding.</param>
    /// <returns>The conversation to persist on the new job, or <c>null</c> for unbound.</returns>
    public static ConversationId? Resolve(
        string? actionType,
        bool isSystemJob,
        ConversationId? explicitConversationId,
        ConversationId? creatingConversationId,
        ConversationKind? creatingConversationKind = null,
        bool creatingConversationIsDefault = false)
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

        // #3521: the adoption guard. A human-facing thread is not a gap to be filled - it is
        // somebody else's property. Decline and let the first-run CAS mint a cronconv: conversation.
        // Note the asymmetry with the explicit branch above: an EXPLICIT human conversation still
        // wins, because a caller naming a conversation is a decision, not a default.
        if (creatingConversationKind == ConversationKind.HumanAgent || creatingConversationIsDefault)
            return null;

        return creating;
    }
}
