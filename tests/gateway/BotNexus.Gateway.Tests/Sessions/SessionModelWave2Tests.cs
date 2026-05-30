using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SessionModelWave2Tests
{
    [Fact]
    public void SessionStatus_Rename_UsesSealed_NotClosed()
    {
        var names = Enum.GetNames<GatewaySessionStatus>();

        names.ShouldContain(nameof(GatewaySessionStatus.Sealed));
        names.ShouldNotContain("Closed");
    }

    [Fact]
    public void SessionStatusLifecycle_NewSession_StartsActive()
    {
        var session = CreateSession();

        session.Status.ShouldBe(GatewaySessionStatus.Active);
    }

    [Fact]
    public void SessionStatusLifecycle_ActiveToSuspended_Works()
    {
        var session = CreateSession();

        session.Status = GatewaySessionStatus.Suspended;

        session.Status.ShouldBe(GatewaySessionStatus.Suspended);
    }

    [Fact]
    public void SessionStatusLifecycle_ActiveToSealed_Works()
    {
        var session = CreateSession();

        session.Status = GatewaySessionStatus.Sealed;

        session.Status.ShouldBe(GatewaySessionStatus.Sealed);
    }

    [Fact]
    public void SessionStatusLifecycle_SuspendedToSealed_Works()
    {
        var session = CreateSession();
        session.Status = GatewaySessionStatus.Suspended;

        session.Status = GatewaySessionStatus.Sealed;

        session.Status.ShouldBe(GatewaySessionStatus.Sealed);
    }

    [Fact]
    public async Task SessionStatusLifecycle_SuspendedToActive_Works()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.Status = GatewaySessionStatus.Suspended;
        var controller = new SessionsController(store);

        var result = await controller.Resume("s1", CancellationToken.None);

        var resumed = result.Value ?? (result.Result as Microsoft.AspNetCore.Mvc.OkObjectResult)?.Value as GatewaySession;
        resumed.ShouldNotBeNull();
        resumed!.Status.ShouldBe(GatewaySessionStatus.Active);
    }

    [Fact]
    public async Task SessionStatusLifecycle_SealedIsTerminal_CannotResumeFromSealed()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.Status = GatewaySessionStatus.Sealed;
        var controller = new SessionsController(store);

        var result = await controller.Resume("s1", CancellationToken.None);

        result.Result.ShouldBeOfType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>();
        session.Status.ShouldBe(GatewaySessionStatus.Sealed);
    }

    [Fact]
    public void SessionType_UserAgent_IsInteractive()
    {
        var session = CreateSession();

        session.SessionType.ShouldBe(SessionType.UserAgent);
        session.IsInteractive.ShouldBeTrue();
    }

    [Fact]
    public void SessionType_CronChannel_IsNonInteractive()
    {
        // P9-E (#645): cron sessions are now SessionType.UserAgent (proxy for the
        // citizen who scheduled the job, per directive W-2). The non-interactive
        // signal moved to the channel — Session.IsInteractive excludes the "cron"
        // ChannelType so memory flushers / warmup still skip these sessions.
        var session = CreateSession();
        session.SessionType = SessionType.UserAgent;
        session.ChannelType = ChannelKey.From("cron");

        session.IsInteractive.ShouldBeFalse();
    }

    [Fact]
    public void SessionType_SubAgent_IsNonInteractive()
    {
        var session = CreateSession();
        session.SessionType = SessionType.AgentSubAgent;

        session.IsInteractive.ShouldBeFalse();
    }

    [Fact]
    public void SessionType_SetAtCreation_PersistsOnModel()
    {
        // P9-E (#645): exercise AgentSubAgent here since SessionType.Cron was deleted.
        // The intent is to prove the model round-trips a non-default SessionType.
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-typed"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            SessionType = SessionType.AgentSubAgent
        };

        session.SessionType.ShouldBe(SessionType.AgentSubAgent);
    }

    [Fact]
    public async Task Participants_CallerAndConversationParticipants_CoexistAfterP9F()
    {
        // P9-F (#657): participants moved from Session to Conversation. CallerId stays
        // on the session (channel-native wire token + audit) while Participants are now
        // mutated via IConversationStore.AddParticipantsAsync.
        var session = CreateSession();
        session.ConversationId = ConversationId.From($"conv-{Guid.NewGuid():N}");
        session.CallerId = "user-123";

        var conversations = new BotNexus.Gateway.Conversations.InMemoryConversationStore();
        var conversation = await conversations.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = session.ConversationId,
            AgentId = session.AgentId,
            Initiator = CitizenId.Of(UserId.From("user-123"))
        });
        await conversations.AddParticipantsAsync(
            conversation.ConversationId,
            [
                new SessionParticipant { CitizenId = CitizenId.Of(UserId.From("user-123")) },
                new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-a")) }
            ]);

        var reloaded = await conversations.GetAsync(conversation.ConversationId);
        reloaded.ShouldNotBeNull();

        session.CallerId.ShouldBe("user-123");
        reloaded!.Participants.Count().ShouldBe(2);
    }

    [Fact]
    public async Task Participants_CallerId_MapsToFirstUserParticipant_OnConversation()
    {
        // P9-F (#657): the "first User participant matches CallerId" invariant is now
        // satisfied at the conversation level; Session.CallerId remains the audit token.
        var session = CreateSession();
        session.ConversationId = ConversationId.From($"conv-{Guid.NewGuid():N}");
        session.CallerId = "caller-1";

        var conversations = new BotNexus.Gateway.Conversations.InMemoryConversationStore();
        var conversation = await conversations.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = session.ConversationId,
            AgentId = session.AgentId,
            Initiator = CitizenId.Of(UserId.From("caller-1"))
        });
        await conversations.AddParticipantsAsync(
            conversation.ConversationId,
            [
                new SessionParticipant { CitizenId = CitizenId.Of(UserId.From("caller-1")) },
                new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-a")) }
            ]);

        var reloaded = await conversations.GetAsync(conversation.ConversationId);
        reloaded.ShouldNotBeNull();
        var firstUser = reloaded!.Participants.First(p => p.CitizenId.Kind == CitizenKind.User);

        firstUser.CitizenId.AsUser!.Value.Value.ShouldBe(session.CallerId);
    }

    [Fact]
    public void ChannelKey_NormalizesOnConstruction()
    {
        var key = new ChannelKey("  SIGNALR  ");

        key.Value.ShouldBe("signalr");
    }

    [Fact]
    public void ChannelKey_Adoption_SessionStoreUsesChannelKeyType()
    {
        var method = typeof(ISessionStore).GetMethod(nameof(ISessionStore.ListByChannelAsync));

        method.ShouldNotBeNull();
        method!.GetParameters()[1].ParameterType.ShouldBe(typeof(ChannelKey));
    }

    [Fact]
    public void ChannelKey_Adoption_NormalizeChannelKeyHelpersRemoved()
    {
        var candidates = new[]
        {
            typeof(InMemorySessionStore),
            typeof(FileSessionStore),
            typeof(SqliteSessionStore),
            typeof(ChannelHistoryController)
        };

        foreach (var type in candidates)
        {
            var normalizeMethod = type.GetMethod("NormalizeChannelKey", BindingFlags.NonPublic | BindingFlags.Static);
            normalizeMethod.ShouldBeNull($"{type.Name} should use ChannelKey normalization instead of custom helpers");
        }
    }

    [Fact]
    public void MessageRole_Adoption_SessionEntryUsesTypedRole()
    {
        var roleProperty = typeof(SessionEntry).GetProperty(nameof(SessionEntry.Role));

        roleProperty.ShouldNotBeNull();
        roleProperty!.PropertyType.ShouldBe(typeof(MessageRole));
    }

    [Fact]
    public void MessageRole_StaticRoles_WorkInSessionEntries()
    {
        var entries = new[]
        {
            new SessionEntry { Role = MessageRole.User, Content = "hello" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "hi" },
            new SessionEntry { Role = MessageRole.System, Content = "sys" },
            new SessionEntry { Role = MessageRole.Tool, Content = "tool" }
        };

        entries.Select(e => e.Role).ToList().ShouldBe(new[] {
            MessageRole.User,
            MessageRole.Assistant,
            MessageRole.System,
            MessageRole.Tool });
    }

    private static GatewaySession CreateSession()
        => new() { SessionId = SessionId.From($"s-{Guid.NewGuid():N}"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
}

