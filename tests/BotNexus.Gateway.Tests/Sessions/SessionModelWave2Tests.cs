using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using FluentAssertions;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SessionModelWave2Tests
{
    [Fact]
    public void SessionStatus_Rename_UsesSealed_NotClosed()
    {
        var names = Enum.GetNames<GatewaySessionStatus>();

        names.Should().Contain(nameof(GatewaySessionStatus.Sealed));
        names.Should().NotContain("Closed");
    }

    [Fact]
    public void SessionStatusLifecycle_NewSession_StartsActive()
    {
        var session = CreateSession();

        session.Status.Should().Be(GatewaySessionStatus.Active);
    }

    [Fact]
    public void SessionStatusLifecycle_ActiveToSuspended_Works()
    {
        var session = CreateSession();

        session.Status = GatewaySessionStatus.Suspended;

        session.Status.Should().Be(GatewaySessionStatus.Suspended);
    }

    [Fact]
    public void SessionStatusLifecycle_ActiveToSealed_Works()
    {
        var session = CreateSession();

        session.Status = GatewaySessionStatus.Sealed;

        session.Status.Should().Be(GatewaySessionStatus.Sealed);
    }

    [Fact]
    public void SessionStatusLifecycle_SuspendedToSealed_Works()
    {
        var session = CreateSession();
        session.Status = GatewaySessionStatus.Suspended;

        session.Status = GatewaySessionStatus.Sealed;

        session.Status.Should().Be(GatewaySessionStatus.Sealed);
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
        resumed.Should().NotBeNull();
        resumed!.Status.Should().Be(GatewaySessionStatus.Active);
    }

    [Fact]
    public async Task SessionStatusLifecycle_SealedIsTerminal_CannotResumeFromSealed()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Status = GatewaySessionStatus.Sealed;
        var controller = new SessionsController(store);

        var result = await controller.Resume("s1", CancellationToken.None);

        result.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>();
        session.Status.Should().Be(GatewaySessionStatus.Sealed);
    }

    [Fact]
    public void SessionType_UserAgent_IsInteractive()
    {
        var session = CreateSession();

        session.SessionType.Should().Be(SessionType.UserAgent);
        session.IsInteractive.Should().BeTrue();
    }

    [Fact]
    public void SessionType_Cron_IsNonInteractive()
    {
        var session = CreateSession();
        session.SessionType = SessionType.Cron;

        session.IsInteractive.Should().BeFalse();
    }

    [Fact]
    public void SessionType_SubAgent_IsNonInteractive()
    {
        var session = CreateSession();
        session.SessionType = SessionType.AgentSubAgent;

        session.IsInteractive.Should().BeFalse();
    }

    [Fact]
    public void SessionType_SetAtCreation_PersistsOnModel()
    {
        var session = new GatewaySession
        {
            SessionId = "s-typed",
            AgentId = "agent-a",
            SessionType = SessionType.Cron
        };

        session.SessionType.Should().Be(SessionType.Cron);
    }

    [Fact]
    public void Participants_CallerAndParticipants_CoexistDuringMigration()
    {
        var session = CreateSession();
        session.CallerId = "user-123";
        session.Participants.Add(new SessionParticipant { Type = ParticipantType.User, Id = "user-123" });
        session.Participants.Add(new SessionParticipant { Type = ParticipantType.Agent, Id = "agent-a" });

        session.CallerId.Should().Be("user-123");
        session.Participants.Should().HaveCount(2);
    }

    [Fact]
    public void Participants_CallerId_MapsToFirstUserParticipant()
    {
        var session = CreateSession();
        session.CallerId = "caller-1";
        session.Participants.Add(new SessionParticipant { Type = ParticipantType.User, Id = "caller-1" });
        session.Participants.Add(new SessionParticipant { Type = ParticipantType.Agent, Id = "agent-a" });

        var firstUser = session.Participants.First(p => p.Type == ParticipantType.User);

        firstUser.Id.Should().Be(session.CallerId);
    }

    [Fact]
    public void ChannelKey_NormalizesOnConstruction()
    {
        var key = new ChannelKey("  SIGNALR  ");

        key.Value.Should().Be("signalr");
    }

    [Fact]
    public void ChannelKey_Adoption_SessionStoreUsesChannelKeyType()
    {
        var method = typeof(ISessionStore).GetMethod(nameof(ISessionStore.ListByChannelAsync));

        method.Should().NotBeNull();
        method!.GetParameters()[1].ParameterType.Should().Be(typeof(ChannelKey));
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
            normalizeMethod.Should().BeNull($"{type.Name} should use ChannelKey normalization instead of custom helpers");
        }
    }

    [Fact]
    public void MessageRole_Adoption_SessionEntryUsesTypedRole()
    {
        var roleProperty = typeof(SessionEntry).GetProperty(nameof(SessionEntry.Role));

        roleProperty.Should().NotBeNull();
        roleProperty!.PropertyType.Should().Be(typeof(MessageRole));
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

        entries.Select(e => e.Role).Should().ContainInOrder(
            MessageRole.User,
            MessageRole.Assistant,
            MessageRole.System,
            MessageRole.Tool);
    }

    private static GatewaySession CreateSession()
        => new() { SessionId = $"s-{Guid.NewGuid():N}", AgentId = "agent-a" };
}

