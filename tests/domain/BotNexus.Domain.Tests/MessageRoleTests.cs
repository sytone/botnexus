using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

public sealed class MessageRoleTests
{
    [Fact]
    public void MessageRole_KnownValues_WhenAccessed_ShouldExist()
    {
        MessageRole.User.Value.ShouldBe("user");
    }

    [Fact]
    public void MessageRole_FromString_WhenValueIsKnown_ShouldReturnKnownInstance()
    {
        var role = MessageRole.FromString("USER");
        role.ShouldBeSameAs(MessageRole.User);
    }

    [Fact]
    public void MessageRole_FromString_WhenValueIsUnknown_ShouldCreateExtensibleInstance()
    {
        var role = MessageRole.FromString("custom-role");
        role.Value.ShouldBe("custom-role");
    }

    [Fact]
    public void MessageRole_FromString_WhenValueHasDifferentCase_ShouldMatchCaseInsensitively()
    {
        var first = MessageRole.FromString("CUSTOM-ROLE");
        var second = MessageRole.FromString("custom-role");
        first.ShouldBeSameAs(second);
    }

    [Fact]
    public void MessageRole_ImplicitConversion_WhenConvertedToString_ShouldReturnValue()
    {
        string value = MessageRole.Assistant;
        value.ShouldBe("assistant");
    }

    [Fact]
    public void MessageRole_Equals_WhenValuesMatch_ShouldBeTrue()
    {
        var left = MessageRole.FromString("tool");
        var right = MessageRole.Tool;
        left.ShouldBe(right);
    }

    [Fact]
    public void MessageRole_Equals_WhenValuesDiffer_ShouldBeFalse()
    {
        var left = MessageRole.User;
        var right = MessageRole.Assistant;
        left.ShouldNotBe(right);
    }

    [Fact]
    public void MessageRole_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var roundTrip = JsonSerializer.Deserialize<MessageRole>(JsonSerializer.Serialize(MessageRole.System));
        roundTrip.ShouldBe(MessageRole.System);
    }

    [Fact]
    public async Task MessageRole_FromString_WhenCalledConcurrently_ShouldBeThreadSafe()
    {
        var tasks = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => MessageRole.FromString("thread-role")))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        results.Distinct().Count().ShouldBe(1);
    }
}
