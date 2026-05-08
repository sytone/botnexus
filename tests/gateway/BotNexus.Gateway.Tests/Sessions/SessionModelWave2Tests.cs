using System.Reflection;
using BotNexus.Domain.Primitives;
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
        var session = await store.GetOrCreateAsync("s1", "agent-a");
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
        var session = await store.GetOrCreateAsync("s1", "agent-a");
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
    public void SessionType_Cron_IsNonInteractive()
    {
        var session = CreateSession();
        session.SessionType = SessionType.Cron;

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
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-typed"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            SessionType = SessionType.Cron
        };

        session.SessionType.ShouldBe(SessionType.Cron);
    }

    [Fact]
    public void Participants_CallerAndParticipants_CoexistDuringMigration()
    {
        var session = CreateSession();
        session.CallerId = "user-123";
        session.Participants.Add(new SessionParticipant { Type = ParticipantType.User, Id = "user-123" });
        session.Participants.Add(new SessionParticipant { Type = ParticipantType.Agent, Id = "agent-a" });

        session.CallerId.ShouldBe("user-123");
        session.Participants.Count().ShouldBe(2);
    }

    [Fact]
    public void Participants_CallerId_MapsToFirstUserParticipant()
    {
        var session = CreateSession();
        session.CallerId = "caller-1";
        session.Participants.Add(new SessionParticipant { Type = ParticipantType.User, Id = "caller-1" });
        session.Participants.Add(new SessionParticipant { Type = ParticipantType.Agent, Id = "agent-a" });

        var firstUser = session.Participants.First(p => p.Type == ParticipantType.User);

        firstUser.Id.ShouldBe(session.CallerId);
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
        => new() { SessionId = $"s-{Guid.NewGuid():N}", AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
}

