using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class SessionStatusTests
{
    [Fact]
    public void SessionStatus_KnownValues_WhenAccessed_ShouldExist()
    {
        SessionStatus.Active.Value.ShouldBe("active");
    }

    [Fact]
    public void SessionStatus_FromString_WhenValueIsKnown_ShouldReturnKnownInstance()
    {
        var status = SessionStatus.FromString("ACTIVE");
        status.ShouldBeSameAs(SessionStatus.Active);
    }

    [Fact]
    public void SessionStatus_FromString_WhenValueIsUnknown_ShouldCreateExtensibleInstance()
    {
        var status = SessionStatus.FromString("paused-for-maintenance");
        status.Value.ShouldBe("paused-for-maintenance");
    }

    [Fact]
    public void SessionStatus_FromString_WhenValueHasDifferentCase_ShouldMatchCaseInsensitively()
    {
        var first = SessionStatus.FromString("CUSTOM-STATUS");
        var second = SessionStatus.FromString("custom-status");
        first.ShouldBeSameAs(second);
    }

    [Fact]
    public void SessionStatus_ImplicitConversion_WhenConvertedToString_ShouldReturnValue()
    {
        string value = SessionStatus.Sealed;
        value.ShouldBe("sealed");
    }

    [Fact]
    public void SessionStatus_Equals_WhenValuesMatch_ShouldBeTrue()
    {
        var left = SessionStatus.FromString("suspended");
        var right = SessionStatus.Suspended;
        left.ShouldBe(right);
    }

    [Fact]
    public void SessionStatus_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = SessionStatus.Active;
        var right = SessionStatus.Sealed;
        left.ShouldNotBe(right);
    }

    [Fact]
    public void SessionStatus_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var roundTrip = JsonSerializer.Deserialize<SessionStatus>(JsonSerializer.Serialize(SessionStatus.Suspended));
        roundTrip.ShouldBe(SessionStatus.Suspended);
    }

    [Fact]
    public async Task SessionStatus_FromString_WhenCalledConcurrently_ShouldBeThreadSafe()
    {
        var tasks = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => SessionStatus.FromString("thread-status")))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        results.Distinct().Count().ShouldBe(1);
    }
}
