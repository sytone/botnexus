using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using Shouldly;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Pins the actor-pseudonym scheme (#2442). The digest form is a <b>security-correlation
/// contract</b>: historical security events stored in logs carry pseudonyms produced by the
/// five private <c>HashActor</c> copies this primitive replaced. If the digest changes,
/// correlation across that history silently breaks, so the vectors below are golden values
/// captured from the CURRENT (pre-refactor) implementation
/// (SHA-256 -&gt; first 8 bytes -&gt; lowercase hex, UTF-8 input, invariant culture,
/// null/empty treated as the empty string).
/// </summary>
public sealed class ActorPseudonymTests
{
    // Golden vectors. Derived from the scheme implemented by the five pre-#2442 HashActor
    // copies. Hard-coded deliberately: re-deriving them in the test would make the test a
    // tautology and would not catch a scheme change.
    [Theory]
    [InlineData("", "e3b0c44298fc1c14")]
    [InlineData("agent-farnsworth", "886183829f3e4f3b")]
    [InlineData("coding-agent", "cc928dc9c608fc43")]
    [InlineData("s_1234567890abcdef", "be81137aa48b1a74")]
    [InlineData("node-alpha", "c0b71775288d224a")]
    [InlineData("Agent-Farnsworth", "6d52adbde03b0c8b")]
    public void For_ProducesGoldenDigest(string input, string expected) =>
        ActorPseudonym.For(input).ShouldBe(expected);

    [Fact]
    public void For_NullIsTreatedAsEmpty() =>
        ActorPseudonym.For(null).ShouldBe("e3b0c44298fc1c14");

    [Fact]
    public void For_ProducesSixteenLowercaseHexChars()
    {
        var value = ActorPseudonym.For("some-actor-id");
        value.Length.ShouldBe(16);
        value.ShouldAllBe(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    [Fact]
    public void For_IsCaseSensitiveOnInput() =>
        ActorPseudonym.For("abc").ShouldNotBe(ActorPseudonym.For("ABC"));

    [Fact]
    public void For_DiffersPerInput() =>
        ActorPseudonym.For("a").ShouldNotBe(ActorPseudonym.For("b"));

    /// <summary>
    /// Stability across two independently constructed descriptors - the property that makes the
    /// pseudonym usable as a correlation key.
    /// </summary>
    [Fact]
    public void AgentDescriptor_Pseudonym_IsStableAcrossIndependentInstances()
    {
        var a = NewDescriptor("coding-agent");
        var b = NewDescriptor("coding-agent");

        a.ShouldNotBeSameAs(b);
        a.Pseudonym.ShouldBe(b.Pseudonym);
    }

    /// <summary>
    /// "Across process restarts" reduces to "purely a function of the id, with no process-local
    /// state (no random seed, no <c>string.GetHashCode</c>, no static counter)". Pinning the
    /// descriptor's pseudonym to the golden constant proves exactly that: a run in any process
    /// must reproduce this literal.
    /// </summary>
    [Fact]
    public void AgentDescriptor_Pseudonym_MatchesGoldenDigestForAgentId() =>
        NewDescriptor("coding-agent").Pseudonym.ShouldBe("cc928dc9c608fc43");

    [Fact]
    public void AgentDescriptor_Pseudonym_DiffersPerAgentId() =>
        NewDescriptor("coding-agent").Pseudonym.ShouldNotBe(NewDescriptor("chat-assistant").Pseudonym);

    [Fact]
    public void AgentDescriptor_Pseudonym_NeverEqualsRawAgentId()
    {
        var descriptor = NewDescriptor("coding-agent");
        descriptor.Pseudonym.ShouldNotBe(descriptor.AgentId.Value);
        descriptor.Pseudonym.ShouldNotContain("coding-agent");
    }

    private static AgentDescriptor NewDescriptor(string agentId) => new()
    {
        AgentId = AgentId.From(agentId),
        DisplayName = agentId,
        ModelId = "test-model",
        ApiProvider = "test-provider",
    };
}
