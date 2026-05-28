using System.Linq;
using System.Reflection;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function enforcing the sub-PR 6.3 (<c>#586</c>) invariant:
/// the three legacy stringly-typed routing-override properties
/// (<c>TargetAgentId</c>, <c>SessionId</c>, <c>ConversationId</c>) have been
/// deleted from <see cref="InboundMessage"/> in favour of the single
/// strongly-typed <see cref="InboundMessage.RoutingHints"/> projection.
/// </summary>
/// <remarks>
/// <para>
/// Sub-PR 6.3 (<c>#586</c>) also removed the regex-based
/// <c>InboundMessageOverrideFenceTests</c>. That fence prevented production code
/// from *reading* the legacy fields and is structurally replaced by the C#
/// compiler — those member names no longer exist, so any reader fails to
/// compile. However, the compiler does not stop a future contributor from
/// *re-introducing* the deleted properties on <see cref="InboundMessage"/>;
/// that re-introduction would silently allow new code to depend on the legacy
/// shape again.
/// </para>
/// <para>
/// This shape-pin uses reflection (not regex) to assert the deleted properties
/// cannot reappear on the public surface of <see cref="InboundMessage"/>.
/// Reflection-based pins are robust to source formatting and the regex pitfalls
/// caught by the architecture-test self-tests across the suite.
/// </para>
/// </remarks>
public sealed class InboundMessageShapeFenceTests
{
    /// <summary>
    /// The three property names deleted in sub-PR 6.3 (#586). Re-introducing
    /// any of them on <see cref="InboundMessage"/> revives the weakly-typed
    /// routing-override anti-pattern.
    /// </summary>
    private static readonly string[] DeletedPropertyNames =
    {
        "TargetAgentId",
        "SessionId",
        "ConversationId",
    };

    /// <summary>
    /// <see cref="InboundMessage"/> must not declare any of the deleted
    /// stringly-typed routing-override properties. The sole routing-override
    /// surface is <see cref="InboundMessage.RoutingHints"/>.
    /// </summary>
    [Fact]
    public void InboundMessage_DoesNotDeclare_DeletedRoutingOverrideProperties()
    {
        var properties = typeof(InboundMessage)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var reintroduced = DeletedPropertyNames
            .Where(name => properties.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        reintroduced.ShouldBeEmpty(
            "InboundMessage declares one or more properties that were deleted in sub-PR 6.3 (#586). " +
            "These weakly-typed routing-override fields were replaced by the single strongly-typed " +
            "InboundMessage.RoutingHints projection (InboundMessageRoutingHints). Re-introducing them " +
            "revives the routing combinatorics issue tracked by umbrella #579. If a channel adapter " +
            "needs to express an agent/session/conversation override, set RoutingHints with a typed " +
            "InboundMessageRoutingHints record or use InboundMessageRoutingHints.LiftFromStrings(...) " +
            "to normalise string-sourced wire values.\n" +
            "Re-introduced properties:\n  " + string.Join("\n  ", reintroduced));
    }

    /// <summary>
    /// Self-test: this fence depends on <see cref="InboundMessage"/> exposing the
    /// typed <see cref="InboundMessage.RoutingHints"/> property as the replacement
    /// surface. If that property is itself deleted or renamed, the fence above
    /// silently becomes vacuous (it would still pass even if the routing-hint
    /// surface vanished entirely). Pin the positive replacement shape here so
    /// the suite catches removal of the typed property too.
    /// </summary>
    [Fact]
    public void InboundMessage_Declares_TypedRoutingHintsProperty()
    {
        var property = typeof(InboundMessage)
            .GetProperty("RoutingHints", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        property.ShouldNotBeNull(
            "InboundMessage.RoutingHints is the sub-PR 6.3 (#586) replacement for the deleted " +
            "stringly-typed routing-override fields. If it is removed or renamed, the deletion fence " +
            "above is vacuous and the legacy fields could be silently revived.");

        property!.PropertyType.ShouldBe(
            typeof(InboundMessageRoutingHints),
            "InboundMessage.RoutingHints must carry the typed InboundMessageRoutingHints projection. " +
            "Replacing the type with a string or a bag silently revives the legacy weakly-typed shape.");

        // RoutingHints? — Nullable<T> is annotated, not a struct wrapper, so we
        // assert the underlying property type is the record and rely on nullable
        // reference annotations for the "?" half.
    }
}
