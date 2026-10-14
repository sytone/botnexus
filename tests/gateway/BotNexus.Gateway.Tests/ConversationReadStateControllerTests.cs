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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

public sealed class ConversationReadStateControllerTests
{
    [Fact]
    public async Task Advance_DerivesReaderFromClaims_AndKeepsReadersIsolated()
    {
        var fixture = await CreateFixtureAsync();
        var readerA = fixture.CreateController(Authenticated("reader-a"));
        var readerB = fixture.CreateController(Authenticated("reader-b"));

        var advanced = await readerA.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None);
        var otherReader = await readerB.GetReadState(fixture.ConversationId.Value, CancellationToken.None);

        ReadResponse(advanced).Position.ShouldBe(10);
        ((NoContentResult)otherReader).ShouldNotBeNull();
    }

    [Fact]
    public async Task Advance_IsMonotonicAndIdempotent()
    {
        var fixture = await CreateFixtureAsync();
        var controller = fixture.CreateController(Authenticated("reader-a"));

        var first = ReadResponse(await controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None));
        var delayed = ReadResponse(await controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(8)),
            CancellationToken.None));
        var retry = ReadResponse(await controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None));

        first.Version.ShouldBe(1);
        delayed.Position.ShouldBe(10);
        delayed.Version.ShouldBe(1);
        retry.Version.ShouldBe(1);
    }

    [Fact]
    public async Task Advance_WhenCallerCannotAccessOwningAgent_ReturnsNotFoundWithoutMutation()
    {
        var fixture = await CreateFixtureAsync();
        var controller = fixture.CreateController(
            Authenticated("reader-a"),
            new GatewayCallerIdentity { CallerId = "scoped", AllowedAgents = ["other-agent"] });

        var result = await controller.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(10)),
            CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
        (await fixture.ReadStates.GetAsync("world-test", ConversationReaderId.From("reader-a"), fixture.ConversationId))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Advance_UnauthenticatedLocalClientsShareServerOwnedReader()
    {
        var fixture = await CreateFixtureAsync();
        var firstDevice = fixture.CreateController(new ClaimsPrincipal());
        var secondDevice = fixture.CreateController(new ClaimsPrincipal());

        await firstDevice.AdvanceReadState(
            fixture.ConversationId.Value,
            new AdvanceConversationReadStateRequest(ConversationReadPosition.From(4)),
            CancellationToken.None);
        var loaded = await secondDevice.GetReadState(fixture.ConversationId.Value, CancellationToken.None);

        ReadResponse(loaded).Position.ShouldBe(4);
        (await fixture.ReadStates.GetAsync(
            "world-test", ConversationReaderIdentity.LocalOwner, fixture.ConversationId)).ShouldNotBeNull();
    }

    private static ConversationReadStateResponse ReadResponse(ActionResult result) =>
        ((OkObjectResult)result).Value.ShouldBeOfType<ConversationReadStateResponse>();

    private static ClaimsPrincipal Authenticated(string subject) =>
        new(new ClaimsIdentity([new Claim(ConversationReaderIdentity.SubClaimType, subject)], "oidc"));

    private static async Task<Fixture> CreateFixtureAsync()
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

    private sealed record Fixture(
        InMemoryConversationStore Conversations,
        InMemoryConversationReadStateStore ReadStates,
        ConversationId ConversationId)
    {
        public ConversationsController CreateController(
            ClaimsPrincipal principal,
            GatewayCallerIdentity? caller = null)
        {
            var controller = new ConversationsController(
                Conversations,
                new InMemorySessionStore(),
                readStateStore: ReadStates,
                worldContext: new TestWorldContext());
            var context = new DefaultHttpContext { User = principal };
            if (caller is not null)
                context.Items["BotNexus.Gateway.CallerIdentity"] = caller;
            controller.ControllerContext = new ControllerContext { HttpContext = context };
            return controller;
        }
    }

    private sealed class TestWorldContext : IWorldContext
    {
        public BotNexus.Domain.WorldIdentity Current { get; } = new()
        {
            Id = "world-test",
            Name = "Test"
        };
    }
}
