using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Pins the production creation seam for an agent's <b>default conversation</b> (issue #2488).
/// </summary>
/// <remarks>
/// <para>
/// Before #2488 every <c>ConversationFactory</c> factory hard-coded <c>isDefault: false</c> and no
/// caller in <c>src/**</c> ever passed <c>true</c>, so <see cref="Conversation.IsDefault"/> was
/// read-only plumbing: the column, the DTOs, the portal ordering and the cron retention exemption
/// all read a value nothing could set. These tests exist to make that regression impossible to
/// re-introduce silently - each one fails if the seam reverts to a hard-coded <c>false</c>.
/// </para>
/// <para>
/// The default conversation is minted through its own intent-revealing factory rather than by
/// passing a boolean to <see cref="ConversationFactory.CreateForChannel"/>, for the same reason the
/// rest of the seam is shaped that way (#2310): the origin is chosen by <em>which factory you
/// call</em>, so a caller cannot silently omit the intent.
/// </para>
/// </remarks>
public sealed class DefaultConversationFactoryTests
{
    private static readonly AgentId Agent = AgentId.From("agent-2488");
    private static readonly ConversationId Id = ConversationId.From("conv:default-test");

    [Fact]
    public void CreateDefaultForAgent_StampsIsDefaultTrue()
    {
        // The whole point of the issue: a production seam that yields IsDefault = true.
        // Reverting the factory to a hard-coded false reddens this by name.
        var conversation = ConversationFactory.CreateDefaultForAgent(Id, Agent);

        conversation.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void CreateDefaultForAgent_StampsChannelProvenanceAndUserFacingVisibility()
    {
        var conversation = ConversationFactory.CreateDefaultForAgent(Id, Agent);

        // The default conversation is the agent's general human-facing home, so it carries the
        // same provenance as any other channel-originated conversation. Visibility must be
        // UserFacing or the portal auto-select could never see it.
        conversation.Source.ShouldBe(ConversationSource.Channel);
        conversation.Kind.ShouldBe(ConversationKind.HumanAgent);
        conversation.Visibility.ShouldBe(ConversationVisibility.UserFacing);
        conversation.Status.ShouldBe(ConversationStatus.Active);
        conversation.AgentId.ShouldBe(Agent);
        conversation.ConversationId.ShouldBe(Id);
    }

    [Fact]
    public void CreateDefaultForAgent_UsesCanonicalTitle_WhenNoneSupplied()
    {
        var conversation = ConversationFactory.CreateDefaultForAgent(Id, Agent);

        conversation.Title.ShouldBe(ConversationFactory.DefaultConversationTitle);
    }

    [Fact]
    public void CreateDefaultForAgent_HonoursExplicitTitle()
    {
        var conversation = ConversationFactory.CreateDefaultForAgent(Id, Agent, title: "Home base");

        conversation.Title.ShouldBe("Home base");
    }

    [Fact]
    public void CreateDefaultForAgent_StampsCreatedAndUpdatedFromOneClockRead()
    {
        var stamp = DateTimeOffset.UtcNow.AddDays(-3);

        var conversation = ConversationFactory.CreateDefaultForAgent(Id, Agent, timestamp: stamp);

        conversation.CreatedAt.ShouldBe(stamp);
        conversation.UpdatedAt.ShouldBe(stamp);
    }

    // ── Sad paths: the other factories must NOT mint defaults ──────────────────

    [Fact]
    public void CreateForChannel_DoesNotMintDefault_ByDefault()
    {
        ConversationFactory.CreateForChannel(Id, Agent).IsDefault.ShouldBeFalse();
    }

    [Fact]
    public void CreateForAgent_NeverMintsDefault()
    {
        ConversationFactory
            .CreateForAgent(ConversationKind.HumanAgent, Id, Agent)
            .IsDefault.ShouldBeFalse();
    }

    [Fact]
    public void CreateForCron_NeverMintsDefault()
    {
        ConversationFactory.CreateForCron(Id, Agent).IsDefault.ShouldBeFalse();
    }

    [Fact]
    public void CreateForWebhook_NeverMintsDefault()
    {
        ConversationFactory.CreateForWebhook(Id, Agent).IsDefault.ShouldBeFalse();
    }

    [Fact]
    public void CreateForSubAgent_NeverMintsDefault()
    {
        ConversationFactory
            .CreateForSubAgent(Id, Agent, ConversationId.From("conv:parent"))
            .IsDefault.ShouldBeFalse();
    }

    [Fact]
    public void CreateForRalph_NeverMintsDefault()
    {
        ConversationFactory
            .CreateForRalph(Id, Agent, instructions: "loop")
            .IsDefault.ShouldBeFalse();
    }
}
