using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class SessionTypeTests
{
    [Fact]
    public void SessionType_KnownValues_WhenAccessed_ShouldExist()
    {
        SessionType.UserAgent.Value.ShouldBe("user-agent");
    }

    [Fact]
    public void SessionType_FromString_WhenValueIsKnown_ShouldReturnKnownInstance()
    {
        var type = SessionType.FromString("USER-AGENT");
        type.ShouldBeSameAs(SessionType.UserAgent);
    }

    [Fact]
    public void SessionType_FromString_WhenValueIsUnknown_ShouldCreateExtensibleInstance()
    {
        var type = SessionType.FromString("internal-trigger");
        type.Value.ShouldBe("internal-trigger");
    }

    [Fact]
    public void SessionType_FromString_WhenValueHasDifferentCase_ShouldMatchCaseInsensitively()
    {
        var first = SessionType.FromString("CUSTOM-TYPE");
        var second = SessionType.FromString("custom-type");
        first.ShouldBeSameAs(second);
    }

    [Fact]
    public void SessionType_ImplicitConversion_WhenConvertedToString_ShouldReturnValue()
    {
        string value = SessionType.AgentSelf;
        value.ShouldBe("agent-self");
    }

    [Theory]
    [InlineData("soul", "agent-self")]
    [InlineData("SOUL", "agent-self")]
    [InlineData("heartbeat", "agent-self")]
    [InlineData("cron", "user-agent")]
    public void SessionType_FromString_WhenValueIsLegacyTriggerName_ShouldMigrateToCanonical(string legacy, string canonical)
    {
        // P9-E (#645): the old SessionType.Soul/Cron/Heartbeat values were proxy-trigger
        // discriminators; the registry now collapses them to the canonical session shape
        // ("agent-self"/"agent-self"/"user-agent" respectively). The proxy origin lives
        // on SessionEntry.Trigger. FromString carries the migration so every JSON path
        // and string-keyed lookup benefits uniformly.
        var migrated = SessionType.FromString(legacy);
        migrated.Value.ShouldBe(canonical);
    }

    [Fact]
    public void SessionType_Equals_WhenValuesMatch_ShouldBeTrue()
    {
        var left = SessionType.FromString("agent-subagent");
        var right = SessionType.AgentSubAgent;
        left.ShouldBe(right);
    }

    [Fact]
    public void SessionType_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = SessionType.UserAgent;
        var right = SessionType.AgentSelf;
        left.ShouldNotBe(right);
    }

    [Fact]
    public void SessionType_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var roundTrip = JsonSerializer.Deserialize<SessionType>(JsonSerializer.Serialize(SessionType.AgentAgent));
        roundTrip.ShouldBe(SessionType.AgentAgent);
    }

    [Fact]
    public async Task SessionType_FromString_WhenCalledConcurrently_ShouldBeThreadSafe()
    {
        var tasks = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => SessionType.FromString("thread-type")))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        results.Distinct().Count().ShouldBe(1);
    }
}
