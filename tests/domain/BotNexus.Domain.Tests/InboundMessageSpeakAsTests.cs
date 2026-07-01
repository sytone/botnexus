using System.Linq;
using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Covers the optional <see cref="InboundMessage.SpeakAs"/> field added as the
/// domain foundation for post-as-assistant (#1649, Step 1/3 of #1547), plus the
/// <see cref="InboundMessage.DeriveChannelPostRole"/> Hybrid role-derivation wired
/// in Step 2/3 (#1650). The field carries the intended message role for
/// agent-initiated posts; <c>null</c> means "no override -- derive the role from
/// the sender kind." The derivation tests pin the Hybrid rule: an explicit
/// <see cref="InboundMessage.SpeakAs"/> is always honoured, otherwise an
/// agent-kind sender defaults to <see cref="MessageRole.Assistant"/> and a
/// user-kind sender stays <see cref="MessageRole.User"/>.
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

    // --- DeriveChannelPostRole: the Hybrid rule (#1650, Step 2/3 of #1547) ---

    [Fact]
    public void DeriveChannelPostRole_AgentSender_NoSpeakAs_ShouldBeAssistant()
    {
        // AC 1: an agent post with no speak_as override defaults to assistant --
        // the agent is speaking as itself (fixes general-channel pushes stamped
        // as user). This is the core behaviour change of the Hybrid fix.
        var message = CreateMessage() with
        {
            Sender = CitizenId.Of(AgentId.From("keel")),
        };

        message.DeriveChannelPostRole().ShouldBe(MessageRole.Assistant);
    }

    [Fact]
    public void DeriveChannelPostRole_AgentSender_ExplicitUserSpeakAs_ShouldBeUser()
    {
        // AC 2: an agent post with an explicit speak_as:"user" override persists
        // as user -- the on-behalf-of-user kickoff case. The explicit override
        // wins over the agent-kind default.
        var message = CreateMessage() with
        {
            Sender = CitizenId.Of(AgentId.From("keel")),
            SpeakAs = MessageRole.User,
        };

        message.DeriveChannelPostRole().ShouldBe(MessageRole.User);
    }

    [Fact]
    public void DeriveChannelPostRole_HumanSender_NoSpeakAs_ShouldBeUser()
    {
        // AC 3: a human inbound message is unchanged -- a user-kind sender with no
        // override stays user, exactly as before the Hybrid fix.
        var message = CreateMessage() with
        {
            Sender = CitizenId.Of(UserId.From("alice")),
        };

        message.DeriveChannelPostRole().ShouldBe(MessageRole.User);
    }

    [Fact]
    public void DeriveChannelPostRole_AgentSender_ExplicitAssistantSpeakAs_ShouldBeAssistant()
    {
        // An explicit speak_as:"assistant" on an agent sender is honoured (and
        // coincides with the agent-kind default) -- the override path returns the
        // stated role verbatim rather than re-deriving.
        var message = CreateMessage() with
        {
            Sender = CitizenId.Of(AgentId.From("keel")),
            SpeakAs = MessageRole.Assistant,
        };

        message.DeriveChannelPostRole().ShouldBe(MessageRole.Assistant);
    }

    [Fact]
    public void DeriveChannelPostRole_HumanSender_ExplicitSpeakAs_IsHonoured()
    {
        // The override is unconditional: when SpeakAs is set it is honoured
        // regardless of sender kind, so the derivation never silently discards a
        // caller-supplied role.
        var message = CreateMessage() with
        {
            Sender = CitizenId.Of(UserId.From("alice")),
            SpeakAs = MessageRole.Assistant,
        };

        message.DeriveChannelPostRole().ShouldBe(MessageRole.Assistant);
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
