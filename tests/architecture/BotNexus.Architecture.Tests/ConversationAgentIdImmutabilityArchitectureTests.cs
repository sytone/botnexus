using System.Reflection;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-H (#662) "Conversation.AgentId
/// is immutable" contract:
/// <list type="bullet">
///   <item>
///     <see cref="Conversation.AgentId"/> exposes only an <c>init</c>-style setter. A
///     <c>set</c> setter would let callers re-bind a persisted conversation to a different
///     owning agent and silently invalidate the <see cref="IAgentIdentityResolver"/> cache,
///     since the resolver memoizes the agent-id-per-conversation lookup for the lifetime of
///     the process.
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The cache safety story for the entire P9-H phase rests on this single invariant. If
/// you find yourself wanting to add a public setter to re-bind a conversation, add a
/// dedicated re-binding API on <see cref="IConversationStore"/> that also invalidates
/// the resolver instead.
/// </para>
/// </remarks>
public sealed class ConversationAgentIdImmutabilityArchitectureTests
{
    [Fact]
    public void Conversation_AgentId_IsInitOnly()
    {
        var prop = typeof(Conversation).GetProperty(
            nameof(Conversation.AgentId),
            BindingFlags.Public | BindingFlags.Instance);

        prop.ShouldNotBeNull("P9-H contract: Conversation.AgentId must exist.");
        prop.CanRead.ShouldBeTrue();

        var setter = prop.GetSetMethod(nonPublic: false);
        setter.ShouldNotBeNull(
            "P9-H contract: Conversation.AgentId must expose an init setter — without one " +
            "the immutable record builder pattern (`with { AgentId = ... }`) breaks for tests.");

        // An `init` setter is a public method whose return-parameter modreqs include
        // System.Runtime.CompilerServices.IsExternalInit. A `set` setter does not.
        var requiredModifiers = setter.ReturnParameter.GetRequiredCustomModifiers();
        requiredModifiers.ShouldContain(
            t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit",
            "P9-H contract: Conversation.AgentId must be `init`-only. The cache safety story " +
            "for IAgentIdentityResolver depends on Conversation.AgentId being immutable post " +
            "construction — a regular `set` setter would let a conversation be silently " +
            "re-bound to a different agent and invalidate every cached lookup.");
    }

    [Fact]
    public void Conversation_AgentId_HasNoMutationMethod()
    {
        // Defence in depth: scan for `SetAgentId(...)` shape methods that could let callers
        // sidestep the init-only setter via a normal method call.
        var methods = typeof(Conversation).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        methods.ShouldNotContain(
            m => m.Name.Equals("SetAgentId", StringComparison.OrdinalIgnoreCase)
                || m.Name.Equals("ChangeAgentId", StringComparison.OrdinalIgnoreCase)
                || m.Name.Equals("RebindAgentId", StringComparison.OrdinalIgnoreCase),
            "P9-H contract: Conversation must not expose a mutation method that lets callers " +
            "side-step the init-only AgentId setter. If a re-binding capability is genuinely " +
            "needed, put it on IConversationStore so it can invalidate IAgentIdentityResolver.");
    }
}
