using System.Reflection;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function enforcing write-once immutability on the identity and
/// creation-time properties of the core gateway domain models (issue #2316).
/// </summary>
/// <remarks>
/// <para>
/// A primary key that can be reassigned after construction is the strongest invariant in
/// the model and the one that was least enforced: <c>Conversation.ConversationId</c> was
/// declared <c>get; set;</c> while its siblings <c>AgentId</c> and <c>Source</c> in the very
/// same record were already <c>init</c>-only. Every cached lookup, channel binding, and
/// session parent-link keyed on the id silently goes stale if the id is rebound. The same
/// argument applies to creation-time facts (<c>CreatedAt</c>) and to stamped-at-origin
/// provenance (<c>Initiator</c>, <c>Kind</c>), all of which have documented "write-once"
/// intent that nothing previously enforced.
/// </para>
/// <para>
/// Enforcement follows the established repo pattern (see
/// <see cref="ConversationAgentIdImmutabilityArchitectureTests"/>): an <c>init</c> setter is
/// a public setter whose return parameter carries the
/// <c>System.Runtime.CompilerServices.IsExternalInit</c> required custom modifier; a plain
/// <c>set</c> setter does not. Reflection is the only way to tell the two apart at test time.
/// </para>
/// <para>
/// <b>Explicitly out of scope</b> — properties that are genuinely mutable by design and must
/// stay so: <c>Conversation.WorldId</c> and <c>ConversationSection.WorldId</c> (lazily
/// backfilled/stamped on read by the stores), <c>Conversation.ActiveSessionId</c> (changes
/// over the conversation's life), and <c>Session.ConversationId</c> (~12 real
/// post-construction writes across cron pinning, sub-agent inheritance, legacy backfill, and
/// re-parenting — converting it needs a <c>with</c>-expression migration tracked separately).
/// </para>
/// </remarks>
public sealed class ModelImmutabilityArchitectureTests
{
    /// <summary>
    /// The write-once contract: (declaring type, property name, why it must not be reassignable).
    /// </summary>
    public static TheoryData<Type, string, string> WriteOnceProperties() => new()
    {
        {
            typeof(Conversation), nameof(Conversation.ConversationId),
            "primary key — rebinding it strands every cached lookup, channel binding, and session parent-link"
        },
        {
            typeof(Conversation), nameof(Conversation.AgentId),
            "owning agent — IAgentIdentityResolver memoizes this per conversation (P9-H, #662)"
        },
        {
            typeof(Conversation), nameof(Conversation.CreatedAt),
            "creation-time fact — a row cannot be created twice"
        },
        {
            typeof(Conversation), nameof(Conversation.Initiator),
            "write-once provenance stamped by the origin path; producers must not overwrite it on later saves"
        },
        {
            typeof(Conversation), nameof(Conversation.Kind),
            "pairing topology stamped at creation; rendering is a pure projection over (Kind, Source)"
        },
        {
            typeof(Conversation), nameof(Conversation.Source),
            "origination trigger stamped once by the minting path; an inbound event must never poison it (#2301)"
        },
        {
            typeof(ConversationSection), nameof(ConversationSection.SectionId),
            "primary key of the section row"
        },
        {
            typeof(ConversationSection), nameof(ConversationSection.AgentId),
            "owning sidebar — a section cannot migrate between agents"
        },
        {
            typeof(ConversationSection), nameof(ConversationSection.CreatedAt),
            "creation-time fact"
        },
        {
            typeof(Session), nameof(Session.SessionId),
            "primary key of the session row"
        },
        {
            typeof(Session), nameof(Session.CreatedAt),
            "creation-time fact — session ordering in transcript assembly depends on it"
        },
        {
            typeof(ChannelBinding), nameof(ChannelBinding.BindingId),
            "primary key of the binding"
        },
    };

    [Theory]
    [MemberData(nameof(WriteOnceProperties))]
    public void IdentityProperty_IsInitOnly(Type declaringType, string propertyName, string rationale)
    {
        var prop = declaringType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        prop.ShouldNotBeNull($"#2316 contract: {declaringType.Name}.{propertyName} must exist ({rationale}).");
        prop.CanRead.ShouldBeTrue($"{declaringType.Name}.{propertyName} must be readable.");

        var setter = prop.GetSetMethod(nonPublic: false);
        setter.ShouldNotBeNull(
            $"#2316 contract: {declaringType.Name}.{propertyName} must expose an init setter so object " +
            "initializers and `with` expressions keep working.");

        var requiredModifiers = setter.ReturnParameter.GetRequiredCustomModifiers();
        requiredModifiers.ShouldContain(
            t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit",
            $"#2316 contract: {declaringType.Name}.{propertyName} must be `init`-only, not `set` — {rationale}. " +
            "If a call site needs to change the value, rebuild the record with a `with` expression rather than " +
            "adding a setter back.");
    }

    [Theory]
    [MemberData(nameof(WriteOnceProperties))]
    public void IdentityProperty_HasNoMutationMethodBackDoor(Type declaringType, string propertyName, string rationale)
    {
        _ = rationale;

        // Defence in depth: a `SetX` / `ChangeX` / `RebindX` method would let callers sidestep
        // the init-only setter with a plain method call and re-open exactly the hole this fence closes.
        var forbidden = new[] { "Set" + propertyName, "Change" + propertyName, "Rebind" + propertyName };
        var methods = declaringType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        methods.ShouldNotContain(
            m => forbidden.Contains(m.Name, StringComparer.OrdinalIgnoreCase),
            $"#2316 contract: {declaringType.Name} must not expose a mutation method that side-steps the " +
            $"init-only {propertyName} setter. If a genuine re-binding capability is needed, put it on the " +
            "owning store so it can also invalidate the caches keyed on the old value.");
    }
}
