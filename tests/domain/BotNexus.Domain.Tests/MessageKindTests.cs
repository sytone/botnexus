using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Pins the orthogonal typed message/delivery kind introduced for issue #2149. The kind must be
/// independent of <see cref="MessageRole"/>, default safely to <c>message</c> when absent, and
/// distinguish the sub-agent completion notification from the parent agent's response to it.
/// </summary>
public sealed class MessageKindTests
{
    [Fact]
    public void MessageKind_KnownValues_WhenAccessed_ShouldExposeStableWireTokens()
    {
        MessageKind.Message.Value.ShouldBe("message");
        MessageKind.SubAgentCompletion.Value.ShouldBe("subagent-completion");
        MessageKind.SubAgentResponse.Value.ShouldBe("subagent-response");
    }

    [Fact]
    public void MessageKind_FromString_WhenValueIsKnown_ShouldReturnKnownInstance()
    {
        MessageKind.FromString("subagent-completion").ShouldBeSameAs(MessageKind.SubAgentCompletion);
        MessageKind.FromString("SUBAGENT-RESPONSE").ShouldBeSameAs(MessageKind.SubAgentResponse);
    }

    [Fact]
    public void MessageKind_FromNullableString_WhenNullOrBlank_ShouldDefaultToMessage()
    {
        MessageKind.FromNullableString(null).ShouldBeSameAs(MessageKind.Message);
        MessageKind.FromNullableString("").ShouldBeSameAs(MessageKind.Message);
        MessageKind.FromNullableString("   ").ShouldBeSameAs(MessageKind.Message);
    }

    [Fact]
    public void MessageKind_FromNullableString_WhenExplicitValue_ShouldResolveThatKind()
    {
        MessageKind.FromNullableString("subagent-completion").ShouldBeSameAs(MessageKind.SubAgentCompletion);
    }

    [Fact]
    public void MessageKind_Completion_And_Response_ShouldBeDistinct()
    {
        MessageKind.SubAgentCompletion.ShouldNotBe(MessageKind.SubAgentResponse);
        MessageKind.SubAgentCompletion.Equals(MessageKind.SubAgentResponse).ShouldBeFalse();
    }

    [Fact]
    public void MessageKind_FromString_WhenEmpty_ShouldThrow()
    {
        Action act = () => MessageKind.FromString("  ");
        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void MessageKind_FromString_WhenUnknown_ShouldCreateExtensibleInstance()
    {
        var kind = MessageKind.FromString("future-kind");
        kind.Value.ShouldBe("future-kind");
        MessageKind.FromString("FUTURE-KIND").ShouldBeSameAs(kind);
    }

    [Fact]
    public void MessageKind_JsonRoundTrip_WhenSerializedAndDeserialized_ShouldBeEqual()
    {
        var json = JsonSerializer.Serialize(MessageKind.SubAgentResponse);
        json.ShouldBe("\"subagent-response\"");
        var roundTrip = JsonSerializer.Deserialize<MessageKind>(json);
        roundTrip.ShouldBe(MessageKind.SubAgentResponse);
    }
}
