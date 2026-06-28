using System.Linq;
using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Covers the optional <see cref="InboundMessage.SpeakAs"/> field added as the
/// domain foundation for post-as-assistant (#1649, Step 1/3 of #1547). The field
/// carries the intended message role for agent-initiated posts; <c>null</c> means
/// "no override -- derive the role from the sender kind in Step 2". No consumer
/// reads it yet, so these tests pin only the additive shape: it defaults to null,
/// preserves an explicit value through construction, and round-trips through
/// record value-equality / <c>with</c>.
/// </summary>
public sealed class InboundMessageSpeakAsTests
{
    [Fact]
    public void InboundMessage_Constructor_WithoutSpeakAs_ShouldDefaultToNull()
    {
        var message = CreateMessage();

        message.SpeakAs.ShouldBeNull();
    }

    [Fact]
    public void InboundMessage_WithExplicitSpeakAs_ShouldPreserveValue()
    {
        var message = CreateMessage() with { SpeakAs = MessageRole.Assistant };

        message.SpeakAs.ShouldNotBeNull();
        message.SpeakAs!.ShouldBe(MessageRole.Assistant);
    }

    [Fact]
    public void InboundMessage_WithUserSpeakAs_ShouldPreserveValue()
    {
        var message = CreateMessage() with { SpeakAs = MessageRole.User };

        message.SpeakAs.ShouldBe(MessageRole.User);
    }

    [Fact]
    public void InboundMessage_SpeakAs_ShouldSurviveRecordWithCopy()
    {
        var original = CreateMessage() with { SpeakAs = MessageRole.Assistant };

        // A copy that changes an unrelated field must preserve SpeakAs.
        var copy = original with { Content = "changed" };

        copy.SpeakAs.ShouldBe(MessageRole.Assistant);
        copy.Content.ShouldBe("changed");
    }

    [Fact]
    public void InboundMessage_SpeakAs_DifferentiatesOtherwiseIdenticalCopies()
    {
        // Start from one shared base so every other field (incl. the by-reference
        // Metadata dictionary and the UtcNow Timestamp) is identical between the
        // two copies -- this isolates SpeakAs as the only differing field, so the
        // record's generated equality reflects exactly the SpeakAs change.
        var baseline = CreateMessage();

        var asAssistant = baseline with { SpeakAs = MessageRole.Assistant };
        var alsoAssistant = baseline with { SpeakAs = MessageRole.Assistant };
        var asUser = baseline with { SpeakAs = MessageRole.User };

        asAssistant.ShouldBe(alsoAssistant);
        asAssistant.ShouldNotBe(asUser);
        asAssistant.ShouldNotBe(baseline); // null vs assistant
    }

    [Fact]
    public void InboundMessage_SpeakAs_IsNullableMessageRole()
    {
        var property = typeof(InboundMessage)
            .GetProperty(nameof(InboundMessage.SpeakAs), BindingFlags.Public | BindingFlags.Instance);

        property.ShouldNotBeNull();
        property!.PropertyType.ShouldBe(typeof(MessageRole));

        // Nullable reference annotation: the property must be marked nullable so
        // "no override" is the documented default rather than a required value.
        var nullabilityInfo = new NullabilityInfoContext().Create(property);
        nullabilityInfo.ReadState.ShouldBe(NullabilityState.Nullable);
    }

    private static InboundMessage CreateMessage() => new()
    {
        ChannelType = ChannelKey.From("signalr"),
        SenderId = "sender-1",
        Sender = CitizenId.Of(UserId.From("sender-1")),
        ChannelAddress = ChannelAddress.From("conversation-1"),
        Content = "hello",
    };
}
