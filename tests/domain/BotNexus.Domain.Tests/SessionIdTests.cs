using System.Reflection;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Domain.Tests;

public sealed class SessionIdTests
{
    [Fact]
    public void From_TrimsLeadingAndTrailingWhitespace()
    {
        var result = SessionId.From(" session-1 ");

        result.Value.ShouldBe("session-1");
    }

    [Fact]
    public void From_RejectsNull()
    {
        Action act = () => SessionId.From(null!);

        act.ShouldThrow<ValueObjectValidationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \n   ")]
    public void From_RejectsEmptyOrWhitespace(string value)
    {
        Action act = () => SessionId.From(value);

        var ex = act.ShouldThrow<ValueObjectValidationException>();
        ex.Message.ShouldContain("SessionId");
    }

    [Fact]
    public void Equality_MatchesByValue()
    {
        SessionId.From("session-1").ShouldBe(SessionId.From("session-1"));
        SessionId.From("session-1").ShouldNotBe(SessionId.From("session-2"));
    }

    [Fact]
    public void ToString_ReturnsRawValue()
    {
        SessionId.From("session-1").ToString().ShouldBe("session-1");
    }

    [Fact]
    public void Create_GeneratesNonEmptyValue()
    {
        var sessionId = SessionId.Create();

        sessionId.Value.ShouldNotBeNullOrWhiteSpace();
        sessionId.Value.Length.ShouldBe(32);
    }

    [Fact]
    public void Create_GeneratesDistinctValues()
    {
        SessionId.Create().ShouldNotBe(SessionId.Create());
    }

    [Fact]
    public void ForSubAgent_UsesPinnedFormat()
    {
        var sessionId = SessionId.ForSubAgent("parent-id", " child ");

        sessionId.Value.ShouldBe("parent-id::subagent::child");
        sessionId.IsSubAgent.ShouldBeTrue();
    }

    [Fact]
    public void ForSubAgent_TypedOverload_DelegatesToStringOverload()
    {
        var fromString = SessionId.ForSubAgent("parent-id", "child");
        var fromTyped = SessionId.ForSubAgent(SessionId.From("parent-id"), "child");

        fromTyped.ShouldBe(fromString);
    }

    [Fact]
    public void ForSubAgent_WhenUniqueIdIsEmpty_ShouldThrow()
    {
        Action act = () => SessionId.ForSubAgent("parent-id", " ");

        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ForSoul_UsesPinnedFormat()
    {
        var sessionId = SessionId.ForSoul(AgentId.From("agent-a"), new DateOnly(2026, 1, 15));

        sessionId.Value.ShouldBe("agent-a::soul::2026-01-15");
        // P9-E (#645): the IsSoul substring predicate is deleted per directive G-4 — callers
        // must not sniff soul-ness off the session id. The canonical signal is now
        // Session.Metadata["soulDate"] (set by SoulTrigger.InitializeSoulSession).
    }

    [Fact]
    public void ForSoul_FromDateTimeOffset_TakesUtcDate()
    {
        // 2026-01-15T23:30:00+05:00 == 2026-01-15T18:30:00Z (UTC)
        var timestamp = new DateTimeOffset(2026, 1, 15, 23, 30, 0, TimeSpan.FromHours(5));

        var sessionId = SessionId.ForSoul(AgentId.From("agent-a"), timestamp);

        sessionId.Value.ShouldBe("agent-a::soul::2026-01-15");
    }

    [Fact]
    public void IsAgentConversation_OnLegacyEncodedId_StillReturnsTrue_ForBackCompatRead()
    {
        // Phase 4 / 1b deleted SessionId.ForAgentConversation, but the IsAgentConversation
        // predicate is retained because SessionStoreBase.InferSessionType still reads it to
        // bucket pre-migration sessions persisted with the `::agent-agent::` encoding.
        var legacy = SessionId.From("agent-a::agent-agent::agent-b::abc123");

        legacy.IsAgentConversation.ShouldBeTrue();
    }

    [Theory]
    [InlineData("parent-id::subagent::child")]      // sub-agent shape
    [InlineData("PARENT::SUBAGENT::child")]          // case-insensitive sub-agent
    [InlineData("cron:job-123")]                     // cron prefix
    [InlineData("CRON:job-123")]                     // case-insensitive cron prefix
    [InlineData("cron:job-123:20260617:abc")]        // full cron session shape
    public void IsReservedInternalNamespace_ReturnsTrue_ForGatewayOwnedNamespaces(string value)
    {
        // Guards the SignalR-hub reserved-namespace check: a client must not be able to
        // hand-target an internal sub-agent or cron session via steer/abort/compact/reset.
        SessionId.From(value).IsReservedInternalNamespace.ShouldBeTrue();
    }

    [Theory]
    [InlineData("session-1")]                         // generic client session
    [InlineData("abc123def456")]                      // GUID-style generic
    [InlineData("agent-a::soul::2026-01-15")]         // soul is deliberately NOT reserved here (directive G-4)
    [InlineData("agent-a::agent-agent::agent-b::x")]  // legacy agent-agent is not a client-forbidden target
    [InlineData("not-cron:something")]                // 'cron:' must be a prefix, not a substring
    public void IsReservedInternalNamespace_ReturnsFalse_ForClientAddressableSessions(string value)
    {
        SessionId.From(value).IsReservedInternalNamespace.ShouldBeFalse();
    }

    [Theory]
    [InlineData("cron:job-123")]                     // cron prefix
    [InlineData("CRON:job-123")]                     // case-insensitive
    [InlineData("cron:job-123:20260617:abc")]        // full cron session shape
    public void IsCron_ReturnsTrue_ForCronPrefixedSessions(string value)
    {
        SessionId.From(value).IsCron.ShouldBeTrue();
    }

    [Theory]
    [InlineData("session-1")]                         // generic client session
    [InlineData("parent-id::subagent::child")]        // sub-agent, not cron
    [InlineData("not-cron:something")]                // 'cron:' must be a prefix, not a substring
    [InlineData("agent-a::soul::2026-01-15")]         // soul is not cron
    public void IsCron_ReturnsFalse_ForNonCronSessions(string value)
    {
        SessionId.From(value).IsCron.ShouldBeFalse();
    }

    [Fact]
    public void Json_SerializesAsBareString()
    {
        var json = JsonSerializer.Serialize(SessionId.From("session-1"));

        json.ShouldBe("\"session-1\"");
    }

    [Fact]
    public void Json_DeserializesFromBareString()
    {
        var id = JsonSerializer.Deserialize<SessionId>("\"session-1\"");

        id.ShouldBe(SessionId.From("session-1"));
    }

    [Fact]
    public void Json_RoundTripPreservesValue()
    {
        var original = SessionId.From("session-1");

        var roundTrip = JsonSerializer.Deserialize<SessionId>(JsonSerializer.Serialize(original));

        roundTrip.ShouldBe(original);
    }

    [Fact]
    public void Json_PropertyOnDtoUsesBareStringWithCamelCase()
    {
        var dto = new { session = SessionId.From("session-1"), label = "hello" };

        var json = JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.ShouldBe("{\"session\":\"session-1\",\"label\":\"hello\"}");
    }

    [Fact]
    public void Type_HasNoImplicitStringOperator()
    {
        var implicitOperators = typeof(SessionId)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "op_Implicit")
            .ToArray();

        implicitOperators.ShouldBeEmpty(
            "SessionId must not expose implicit conversions to/from string. " +
            "Use .Value and .From() explicitly.");
    }
}
