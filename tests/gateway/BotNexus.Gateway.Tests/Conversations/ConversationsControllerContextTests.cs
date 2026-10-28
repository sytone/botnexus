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

/// <summary>Current context reads must never activate a handle or cross conversation/session scope.</summary>
public sealed class ConversationsControllerContextTests
{
    private readonly InMemoryConversationStore _conversations = new();
    private readonly InMemorySessionStore _sessions = new();
    private readonly IAgentSupervisor _supervisor = Substitute.For<IAgentSupervisor, IAgentHandleInspector>();
    private readonly ISessionCompactionDiagnostics _policy = Substitute.For<ISessionCompactionDiagnostics>();
    private static readonly AgentId Owner = AgentId.From("owner");

    [Fact]
    public async Task GetContext_TwoConversationsOnOneAgent_UsesOnlyEachActiveSession()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        Resident(first, 101);
        Resident(second, 202);
        var before = DateTimeOffset.UtcNow;

        var a = await ReadAsync(first);
        var b = await ReadAsync(second);

        a.GetProperty("sessionId").GetString().ShouldBe(first.ActiveSessionId?.Value);
        b.GetProperty("sessionId").GetString().ShouldBe(second.ActiveSessionId?.Value);
        a.GetProperty("context").GetProperty("totalEstimatedTokens").GetInt32().ShouldBe(101);
        b.GetProperty("context").GetProperty("totalEstimatedTokens").GetInt32().ShouldBe(202);
        a.GetProperty("status").GetString().ShouldBe("available");
        a.GetProperty("source").GetString().ShouldBe("resident-handle-local-estimate");
        a.GetProperty("observedAtUtc").GetDateTimeOffset().ShouldBeInRange(before, DateTimeOffset.UtcNow);
        a.GetProperty("context").GetProperty("contextWindowTokens").GetInt32().ShouldBe(1000);
        a.GetProperty("context").GetProperty("compactionPolicy").GetProperty("providerPromptTokens").GetInt32().ShouldBe(77);
        a.GetRawText().ShouldNotContain("SECRET");
        // A diagnostic read can call only the supervisor's resident lookup, never GetOrCreate/Prompt/Stop.
        _supervisor.ReceivedCalls().ShouldAllBe(call => call.GetMethodInfo().Name == "GetHandle");
    }

    [Theory]
    [InlineData("no-active-session")]
    [InlineData("unloaded")]
    [InlineData("unsupported")]
    [InlineData("unavailable")]
    public async Task GetContext_UnavailableStates_AreExplicitAndNotZero(string state)
    {
        var conversation = await CreateAsync();
        if (state == "no-active-session")
            await _conversations.SaveAsync(conversation with { ActiveSessionId = null });
        if (state == "unsupported")
        {
            var handle = Substitute.For<IAgentHandle>();
            handle.AgentId.Returns(Owner);
            handle.SessionId.Returns(conversation.ActiveSessionId.GetValueOrDefault());
            Inspector.GetHandle(Owner, handle.SessionId).Returns(handle);
        }
        if (state == "unloaded")
            Inspector.GetHandle(Owner, conversation.ActiveSessionId.GetValueOrDefault()).Returns((IAgentHandle?)null);
        var controller = Controller(state == "unavailable" ? null : _supervisor);

        var json = Json(await controller.GetContextAsync(conversation.ConversationId.Value));

        json.GetProperty("status").GetString().ShouldBe(state);
        json.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("source").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetContext_SessionOwnershipMismatch_ReturnsNoDiagnostics(bool wrongAgent)
    {
        var conversation = await CreateAsync();
        var sessionId = conversation.ActiveSessionId.GetValueOrDefault();
        var sessions = Substitute.For<ISessionStore>();
        sessions.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(new GatewaySession
        {
            SessionId = sessionId,
            AgentId = wrongAgent ? AgentId.From("different") : Owner,
            ConversationId = wrongAgent ? conversation.ConversationId : ConversationId.Create()
        });

        var json = Json(await Controller(_supervisor, sessions).GetContextAsync(conversation.ConversationId.Value));

        json.GetProperty("status").GetString().ShouldBe("unavailable");
        json.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
        _supervisor.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == "GetHandle");
    }

    [Theory]
    [InlineData(ConversationVisibility.InternalHidden)]
    [InlineData(ConversationVisibility.UserFacing)]
    public async Task GetContext_HiddenOrUnauthorized_ReturnsNotFound(ConversationVisibility visibility)
    {
        var conversation = await CreateAsync(visibility);
        var controller = Controller(_supervisor);
        if (visibility == ConversationVisibility.UserFacing)
            controller.HttpContext.Items[GatewayAuthHttpContext.CallerIdentityItemKey] = new GatewayCallerIdentity
            {
                CallerId = "reader", AllowedAgents = ["different"]
            };

        (await controller.GetContextAsync(conversation.ConversationId.Value)).ShouldBeOfType<NotFoundResult>();
        _supervisor.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == "GetHandle");
    }

    [Fact]
    public async Task GetContext_InspectableReadOnly_CanReadWithoutMutation()
    {
        var conversation = await CreateAsync(ConversationVisibility.InspectableReadOnly);
        Resident(conversation, 42);
        (await ReadAsync(conversation)).GetProperty("status").GetString().ShouldBe("available");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task GetContext_ResetOrReplacementDuringPolicyRead_DiscardsOldSnapshot(bool reset, bool idContainsTokenCount)
    {
        // Token-count digits are valid identity data, not evidence of a leaked context snapshot.
        var conversation = await CreateAsync(conversationId: idContainsTokenCount
            ? ConversationId.From("c_31400000000000000000000000000000")
            : null);
        Resident(conversation, 314);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CompactionPolicyDiagnostics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _policy.ResolveAsync(Owner, conversation.ActiveSessionId.GetValueOrDefault(), Arg.Any<CancellationToken>())
            .Returns(_ => { entered.SetResult(); return release.Task; });
        var pending = Controller(_supervisor).GetContextAsync(conversation.ConversationId.Value);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _conversations.SaveAsync(conversation with { ActiveSessionId = reset ? null : SessionId.Create() });
        release.SetResult(new CompactionPolicyDiagnostics());

        var json = Json(await pending);

        json.GetProperty("conversationId").GetString().ShouldBe(conversation.ConversationId.Value);
        json.GetProperty("status").GetString().ShouldBe("binding-changed");
        json.GetProperty("agentId").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("sessionId").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("source").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetContext_HandleEvictedDuringPolicyRead_DiscardsSnapshot()
    {
        var conversation = await CreateAsync();
        Resident(conversation, 55);
        _policy.ResolveAsync(Owner, conversation.ActiveSessionId.GetValueOrDefault(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Inspector.GetHandle(Owner, conversation.ActiveSessionId.GetValueOrDefault()).Returns((IAgentHandle?)null);
                return Task.FromResult<CompactionPolicyDiagnostics?>(null);
            });
        var json = await ReadAsync(conversation);
        json.GetProperty("status").GetString().ShouldBe("unloaded");
        json.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetContext_MissingConversation_ReturnsNotFound()
    {
        (await Controller(_supervisor).GetContextAsync(ConversationId.Create().Value)).ShouldBeOfType<NotFoundResult>();
        _supervisor.ReceivedCalls().ShouldNotContain(call => call.GetMethodInfo().Name == "GetHandle");
    }

    [Fact]
    public async Task GetContext_WrongResidentHandle_ReturnsUnavailable()
    {
        var conversation = await CreateAsync();
        var handle = Substitute.For<IAgentHandle, IAgentHandleInspector>();
        handle.AgentId.Returns(Owner);
        handle.SessionId.Returns(SessionId.Create());
        Inspector.GetHandle(Owner, conversation.ActiveSessionId.GetValueOrDefault()).Returns(handle);

        var json = await ReadAsync(conversation);

        json.GetProperty("status").GetString().ShouldBe("unavailable");
        json.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
        ((IAgentHandleInspector)handle).DidNotReceive().GetContextDiagnostics();
    }

    [Fact]
    public async Task GetContext_SessionSealedDuringPolicyRead_DiscardsSnapshot()
    {
        var conversation = await CreateAsync();
        Resident(conversation, 66);
        var sessionId = conversation.ActiveSessionId.GetValueOrDefault();
        _policy.ResolveAsync(Owner, sessionId, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            var session = await _sessions.GetAsync(sessionId) ?? throw new InvalidOperationException();
            session.Status = SessionStatus.Sealed;
            await _sessions.SaveAsync(session);
            return (CompactionPolicyDiagnostics?)new CompactionPolicyDiagnostics();
        });

        var json = await ReadAsync(conversation);

        json.GetProperty("status").GetString().ShouldBe("unavailable");
        json.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private IAgentHandleInspector Inspector => (IAgentHandleInspector)_supervisor;

    private ConversationsController Controller(IAgentSupervisor? supervisor, ISessionStore? sessions = null) =>
        new(_conversations, sessions ?? _sessions, supervisor: supervisor, compactionDiagnostics: _policy)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private async Task<JsonElement> ReadAsync(Conversation conversation) =>
        Json(await Controller(_supervisor).GetContextAsync(conversation.ConversationId.Value));

    private static JsonElement Json(ActionResult result) =>
        JsonSerializer.SerializeToElement(result.ShouldBeOfType<OkObjectResult>().Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private async Task<Conversation> CreateAsync(
        ConversationVisibility visibility = ConversationVisibility.UserFacing, ConversationId? conversationId = null)
    {
        var conversation = await _conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId ?? ConversationId.Create(), AgentId = Owner, Visibility = visibility,
            Title = "context", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        var session = await _sessions.GetOrCreateAsync(SessionId.Create(), Owner);
        session.ConversationId = conversation.ConversationId;
        await _sessions.SaveAsync(session);
        await _conversations.SaveAsync(conversation with { ActiveSessionId = session.SessionId });
        return await _conversations.GetAsync(conversation.ConversationId) ?? throw new InvalidOperationException();
    }

    private void Resident(Conversation conversation, int tokens)
    {
        var sessionId = conversation.ActiveSessionId.GetValueOrDefault();
        var handle = Substitute.For<IAgentHandle, IAgentHandleInspector>();
        handle.AgentId.Returns(Owner);
        handle.SessionId.Returns(sessionId);
        var diagnostics = (IAgentHandleInspector)handle;
        diagnostics.GetContextDiagnostics().Returns(new ContextDiagnostics
        {
            TotalEstimatedTokens = tokens, SystemPrompt = "SECRET prompt",
            Tools = [new ToolDiagInfo("SECRET tool", "SECRET description", 10)]
        });
        diagnostics.GetContextWindowTokens().Returns(1000);
        Inspector.GetHandle(Owner, sessionId).Returns(handle);
        _policy.ResolveAsync(Owner, sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CompactionPolicyDiagnostics?>(new CompactionPolicyDiagnostics
            {
                ProviderPromptTokens = 77, ProviderMeasurementSource = "provider"
            }));
    }
}
