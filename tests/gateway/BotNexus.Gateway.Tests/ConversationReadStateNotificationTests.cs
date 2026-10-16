using System.Security.Claims;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Extensions.Channels.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ConversationReadStateNotificationTests
{
    [Fact]
    public async Task Advance_NotifiesReaderOnlyAfterVersionAdvances_AndOmitsReaderFromPayload()
    {
        var fixture = await Fixture.CreateAsync();
        var notifier = new RecordingNotifier();
        var controller = fixture.CreateController(Authenticated("reader-a"), [notifier]);

        var first = await controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None);
        var retry = await controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None);
        var lower = await controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(8)),
            CancellationToken.None);

        first.ShouldBeOfType<OkObjectResult>();
        retry.ShouldBeOfType<OkObjectResult>();
        lower.ShouldBeOfType<OkObjectResult>();
        notifier.Notifications.ShouldHaveSingleItem().ShouldBe(
            new Notification(ConversationReaderId.From("reader-a"), fixture.ConversationId, 10, 1));
    }

    [Fact]
    public async Task Advance_ConcurrentEqualPositionsNotifyOnlyForTheAtomicWinner()
    {
        var fixture = await Fixture.CreateAsync();
        var notifier = new RecordingNotifier();
        var controller = fixture.CreateController(Authenticated("reader-a"), [notifier]);

        var requests = Enumerable.Range(0, 8).Select(_ => controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None));
        await Task.WhenAll(requests);

        notifier.Notifications.ShouldHaveSingleItem().Version.ShouldBe(1);
    }

    [Fact]
    public async Task Advance_UnauthorizedReaderDoesNotNotify()
    {
        var fixture = await Fixture.CreateAsync();
        var notifier = new RecordingNotifier();
        var controller = fixture.CreateController(
            Authenticated("reader-a"), [notifier],
            new GatewayCallerIdentity { CallerId = "scoped", AllowedAgents = ["another-agent"] });

        var result = await controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
        notifier.Notifications.ShouldBeEmpty();
    }

    [Fact]
    public async Task Advance_WhenStoreFailsDoesNotNotify()
    {
        var fixture = await Fixture.CreateAsync();
        var notifier = new RecordingNotifier();
        var controller = fixture.CreateController(
            Authenticated("reader-a"), [notifier],
            readStateStore: new FailedReadStateStore());

        await Should.ThrowAsync<InvalidOperationException>(() => controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None));

        notifier.Notifications.ShouldBeEmpty();
    }

    [Fact]
    public async Task Advance_WhenNotifierFails_PersistedSuccessIsReturned()
    {
        var fixture = await Fixture.CreateAsync();
        var notifier = new RecordingNotifier { ThrowOnNotify = true };
        var controller = fixture.CreateController(Authenticated("reader-a"), [notifier]);

        var result = await controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        var state = await fixture.ReadStates.GetAsync(
            "world-test", ConversationReaderId.From("reader-a"), fixture.ConversationId);
        state.ShouldNotBeNull().Position.Value.ShouldBe(10);
        notifier.Notifications.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SignalRNotifier_SendsTypedPayloadOnlyToResolvedReaderUser()
    {
        var proxy = new Mock<IGatewayHubClient>();
        proxy.Setup(client => client.ConversationReadStateChanged(It.IsAny<ConversationReadStateChangedPayload>()))
            .Returns(Task.CompletedTask);
        var requestedUsers = new List<string>();
        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(value => value.User(It.IsAny<string>()))
            .Returns((string userId) => { requestedUsers.Add(userId); return proxy.Object; });
        var context = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        context.SetupGet(value => value.Clients).Returns(clients.Object);
        var notifier = new SignalRConversationReadStateChangeNotifier(context.Object);

        await notifier.NotifyReadStateChangedAsync(
            ConversationReaderId.From("reader-a"), ConversationId.From("conversation-a"),
            ConversationReadPosition.From(12), 3, CancellationToken.None);

        requestedUsers.ShouldBe(["reader-a"]);
        proxy.Verify(client => client.ConversationReadStateChanged(It.Is<ConversationReadStateChangedPayload>(payload =>
            payload.ConversationId == "conversation-a" && payload.Position == 12 && payload.Version == 3)), Times.Once);
        clients.VerifyGet(value => value.All, Times.Never);
        System.Text.Json.JsonSerializer.Serialize(new ConversationReadStateChangedPayload("conversation-a", 12, 3))
            .ShouldNotContain("reader", Case.Insensitive);
    }

    [Fact]
    public void SignalRServiceContributor_RegistersReadStateNotifier()
    {
        var services = new ServiceCollection();

        new SignalRServiceContributor().ConfigureServices(services);

        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IConversationReadStateChangeNotifier)
            && descriptor.ImplementationType == typeof(SignalRConversationReadStateChangeNotifier))
            .ShouldBeTrue();
    }

    private static ClaimsPrincipal Authenticated(string subject) =>
        new(new ClaimsIdentity([new Claim(ConversationReaderIdentity.SubClaimType, subject)], "oidc"));

    private sealed record Notification(
        ConversationReaderId ReaderId, ConversationId ConversationId, long Position, long Version);

    private sealed class RecordingNotifier : IConversationReadStateChangeNotifier
    {
        public List<Notification> Notifications { get; } = [];
        public bool ThrowOnNotify { get; init; }

        public Task NotifyReadStateChangedAsync(
            ConversationReaderId readerId, ConversationId conversationId, ConversationReadPosition position, long version,
            CancellationToken cancellationToken = default)
        {
            Notifications.Add(new Notification(readerId, conversationId, position.Value, version));
            return ThrowOnNotify ? Task.FromException(new InvalidOperationException("transport unavailable")) : Task.CompletedTask;
        }
    }

    private sealed class FailedReadStateStore : IConversationReadStateStore
    {
        public Task<ConversationReadState?> GetAsync(string worldId, ConversationReaderId readerId, ConversationId conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<ConversationReadState?>(null);

        public Task<ConversationReadState> AdvanceAsync(string worldId, ConversationReaderId readerId, ConversationId conversationId, ConversationReadPosition position, CancellationToken cancellationToken = default)
            => Task.FromException<ConversationReadState>(new InvalidOperationException("store unavailable"));

        public Task<ConversationReadStateAdvanceResult> AdvanceWithResultAsync(string worldId, ConversationReaderId readerId, ConversationId conversationId, ConversationReadPosition position, CancellationToken cancellationToken = default)
            => Task.FromException<ConversationReadStateAdvanceResult>(new InvalidOperationException("store unavailable"));
    }

    private sealed record Fixture(
        InMemoryConversationStore Conversations, InMemoryConversationReadStateStore ReadStates, ConversationId ConversationId)
    {
        public static async Task<Fixture> CreateAsync()
        {
            var conversations = new InMemoryConversationStore();
            var conversationId = ConversationId.From("read-state-conversation");
            await conversations.CreateAsync(new Conversation
            {
                ConversationId = conversationId,
                AgentId = AgentId.From("agent-a"),
                Title = "Read state"
            });
            return new Fixture(conversations, new InMemoryConversationReadStateStore(), conversationId);
        }

        public ConversationsController CreateController(
            ClaimsPrincipal principal, IReadOnlyList<IConversationReadStateChangeNotifier> notifiers,
            GatewayCallerIdentity? caller = null, IConversationReadStateStore? readStateStore = null)
        {
            var controller = new ConversationsController(
                Conversations, new InMemorySessionStore(),
                readStateStore: readStateStore ?? ReadStates,
                worldContext: new TestWorldContext(),
                readStateChangeNotifiers: notifiers);
            var context = new DefaultHttpContext { User = principal };
            if (caller is not null)
                context.Items["BotNexus.Gateway.CallerIdentity"] = caller;
            controller.ControllerContext = new ControllerContext { HttpContext = context };
            return controller;
        }
    }

    private sealed class TestWorldContext : IWorldContext
    {
        public BotNexus.Domain.WorldIdentity Current { get; } = new() { Id = "world-test", Name = "Test" };
    }
}
