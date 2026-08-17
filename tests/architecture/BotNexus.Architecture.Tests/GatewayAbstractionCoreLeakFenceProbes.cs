using AliasedCoreUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Probe for <see cref="GatewayAbstractionCoreLeakFenceArchitectureTests.Fence_DetectsAnAliasConcealedReintroduction"/> (#3040 AC4).
///
/// <para>
/// This type reproduces the exact concealment mechanism that hid the original leak: its public
/// member's parameter is spelled with a <c>using</c> alias that reads like a gateway type, and the
/// member's own source line contains no <c>BotNexus.Agent.Core</c> namespace text at all. A
/// source-text or namespace-string fence reports it clean; the metadata-based fence does not.
/// </para>
///
/// <para>
/// It lives in the test assembly on purpose - it must never be reachable from production code, and
/// it must never be added to the fenced assembly list.
/// </para>
/// </summary>
public sealed class AliasConcealedLeakProbe
{
    /// <summary>Publishes an agent-core type through nothing but an alias. Never called.</summary>
    public void LeakThroughAnAlias(AliasedCoreUserMessage message) => _ = message;
}
