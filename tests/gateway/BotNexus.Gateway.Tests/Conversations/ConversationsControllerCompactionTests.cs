using System.Reflection;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>
/// Executable HTTP contract RED for #4982. Route discovery deliberately keeps the baseline
/// compilable: absence of the endpoint is an assertion failure, not a missing-symbol build failure.
/// Runtime admission/eviction races require separate real-handle coverage before implementation ships.
/// </summary>
public sealed class ConversationsControllerCompactionTests
{
    private static readonly AgentId Owner = AgentId.From("owner");
    private readonly InMemoryConversationStore _conversations = new();
    private readonly InMemorySessionStore _sessions = new();
    private readonly IAgentSupervisor _supervisor = Substitute.For<IAgentSupervisor, IAgentHandleInspector>();
    private readonly ISessionCompactionCoordinator _coordinator = Substitute.For<ISessionCompactionCoordinator>();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Compact_RouteExists_AndAcceptsExplicitExpectedSessionId()
    {
        var endpoint = Endpoint();
        endpoint.GetParameters().ShouldContain(parameter => parameter.Name == "conversationId");
        var body = endpoint.GetParameters().Single(parameter =>
            parameter.GetCustomAttribute<FromBodyAttribute>() is not null);
        body.ParameterType.GetProperties().ShouldContain(property =>
            string.Equals(property.Name, "ExpectedSessionId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Compact_MissingConversation_DoesNotInvokeCoordinator()
    {
        var result = await InvokeAsync(Controller(), ConversationId.Create().Value, SessionId.Create().Value);
        result.ShouldBeOfType<NotFoundResult>();
        NoCompaction();
    }

    [Theory]
    [InlineData(ConversationVisibility.InternalHidden)]
    [InlineData(ConversationVisibility.InspectableReadOnly)]
    public async Task Compact_NonWritableVisibility_DoesNotInvokeCoordinator(ConversationVisibility visibility)
    {
        var conversation = await CreateAsync(visibility);
        var result = await InvokeAsync(Controller(), conversation.ConversationId.Value, conversation.ActiveSessionId?.Value);
        StatusCode(result).ShouldBeOneOf(403, 404, 409);
        NoCompaction();
    }

    [Fact]
    public async Task Compact_UnauthorizedAgent_HidesConversationAndDoesNotInspectHandle()
    {
        var conversation = await CreateAsync();
        var controller = Controller();
        controller.HttpContext.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity
        {
            CallerId = "reader", AllowedAgents = ["different-agent"]
        };
        (await InvokeAsync(controller, conversation.ConversationId.Value, conversation.ActiveSessionId?.Value))
            .ShouldBeOfType<NotFoundResult>();
        NoCompaction();
        _supervisor.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Compact_ArchivedConversation_DoesNotInvokeCoordinator()
    {
        var conversation = await CreateAsync();
        await _conversations.SaveAsync(conversation with { Status = ConversationStatus.Archived, ActiveSessionId = null });
        var result = await InvokeAsync(Controller(), conversation.ConversationId.Value, conversation.ActiveSessionId?.Value);
        StatusCode(result).ShouldBeOneOf(403, 404, 409);
        NoCompaction();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Compact_MissingExpectedSessionId_IsRejectedWithoutMutation(string? expectedSessionId)
    {
        var conversation = await CreateAsync();
        StatusCode(await InvokeAsync(Controller(), conversation.ConversationId.Value, expectedSessionId)).ShouldBe(400);
        NoCompaction();
    }

    [Fact]
    public async Task Compact_StaleExpectedSessionId_DoesNotCompactCurrentOrHistoricalSession()
    {
        var conversation = await CreateAsync();
        StatusCode(await InvokeAsync(Controller(), conversation.ConversationId.Value, SessionId.Create().Value))
            .ShouldBe(409);
        NoCompaction();
    }

    [Fact]
    public async Task Compact_NoActiveSession_DoesNotChooseAnAgentDefaultSession()
    {
        var conversation = await CreateAsync();
        await _conversations.SaveAsync(conversation with { ActiveSessionId = null });
        StatusCode(await InvokeAsync(Controller(), conversation.ConversationId.Value, conversation.ActiveSessionId?.Value))
            .ShouldBe(409);
        NoCompaction();
    }

    [Fact]
    public async Task Compact_SealedSession_DoesNotInvokeCoordinator()
    {
        var conversation = await CreateAsync();
        var session = await SessionAsync(conversation);
        session.Status = SessionStatus.Sealed;
        await _sessions.SaveAsync(session);
        StatusCode(await InvokeAsync(Controller(), conversation.ConversationId.Value, session.SessionId.Value))
            .ShouldBe(409);
        NoCompaction();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Compact_SessionOwnershipMismatch_DoesNotInvokeCoordinator(bool wrongAgent)
    {
        var conversation = await CreateAsync();
        var session = await SessionAsync(conversation);
        if (wrongAgent)
            await _conversations.SaveAsync(conversation with { AgentId = AgentId.From("other-agent") });
        else
            session.ConversationId = ConversationId.Create();
        await _sessions.SaveAsync(session);
        StatusCode(await InvokeAsync(Controller(), conversation.ConversationId.Value, session.SessionId.Value))
            .ShouldBe(409);
        NoCompaction();
    }

    [Fact]
    public async Task Compact_UnloadedSession_DoesNotActivateAnAgent()
    {
        var conversation = await CreateAsync();
        var result = await InvokeAsync(Controller(), conversation.ConversationId.Value, conversation.ActiveSessionId?.Value);
        StatusCode(result).ShouldBe(409);
        NoCompaction();
        _supervisor.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == "GetOrCreateAsync");
    }

    [Fact]
    public async Task Compact_OtherConversationsExpectedSession_DoesNotCrossScope()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        var result = await InvokeAsync(Controller(), first.ConversationId.Value, second.ActiveSessionId?.Value);
        StatusCode(result).ShouldBe(409);
        NoCompaction();
        (await _conversations.GetAsync(first.ConversationId))?.ActiveSessionId.ShouldBe(first.ActiveSessionId);
        (await _conversations.GetAsync(second.ConversationId))?.ActiveSessionId.ShouldBe(second.ActiveSessionId);
    }

    private void NoCompaction() => _coordinator.ReceivedCalls().ShouldBeEmpty();

    private ConversationsController Controller()
    {
        var controller = new ConversationsController(_conversations, _sessions,
            supervisor: _supervisor, compactionCoordinator: _coordinator);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static MethodInfo Endpoint()
    {
        var endpoints = typeof(ConversationsController).GetMethods().Where(method =>
            method.GetCustomAttributes<HttpPostAttribute>().Any(attribute =>
                attribute.Template == "{conversationId}/compact")).ToArray();
        endpoints.Length.ShouldBe(1, "#4982 requires exactly one conversation-bound POST compact endpoint");
        return endpoints[0];
    }

    private static async Task<ActionResult> InvokeAsync(ConversationsController controller,
        string conversationId, string? expectedSessionId)
    {
        return await controller.CompactAsync(conversationId,
            new CompactConversationRequest(expectedSessionId), CancellationToken.None);
    }

    private static int StatusCode(ActionResult result) => result switch
    {
        ObjectResult value => value.StatusCode ?? 200,
        StatusCodeResult value => value.StatusCode,
        _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}")
    };

    private async Task<GatewaySession> SessionAsync(Conversation conversation) =>
        await _sessions.GetAsync(conversation.ActiveSessionId.GetValueOrDefault())
            ?? throw new InvalidOperationException("Test session missing.");

    private async Task<Conversation> CreateAsync(ConversationVisibility visibility = ConversationVisibility.UserFacing)
    {
        var conversation = await _conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(), AgentId = Owner, Visibility = visibility,
            Title = "manual compaction", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        var session = await _sessions.GetOrCreateAsync(SessionId.Create(), Owner);
        session.ConversationId = conversation.ConversationId;
        await _sessions.SaveAsync(session);
        await _conversations.SaveAsync(conversation with { ActiveSessionId = session.SessionId });
        return await _conversations.GetAsync(conversation.ConversationId) ?? throw new InvalidOperationException();
    }
}
