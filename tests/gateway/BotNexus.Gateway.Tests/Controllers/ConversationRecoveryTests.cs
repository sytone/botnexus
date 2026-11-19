using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Controllers;

/// <summary>Stopped-work resume must reserve runtime admission, preserve waits, and compare identity.</summary>
public sealed class ConversationRecoveryTests
{
    private readonly InMemoryConversationStore _conversations = new();
    private readonly InMemorySessionStore _sessions = new();
    private readonly IAgentSupervisor _supervisor = Substitute.For<IAgentSupervisor>();
    private readonly IInboundMessageOrchestrator _queue = Substitute.For<IInboundMessageOrchestrator>();
    private readonly IAgentHandle _handle = Substitute.For<IAgentHandle>();
    private readonly IInboundIdleReservation _reservation = Substitute.For<IInboundIdleReservation>();
    private readonly IDisposable _runtime = Substitute.For<IDisposable>();
    private readonly AgentId _agent = AgentId.From("worker");
    private readonly ConversationId _conversation = ConversationId.From("c-recovery");
    private readonly SessionId _session = SessionId.From("s-recovery");
    private InboundExecutionControl? _control;
    private readonly TaskCompletionSource<InboundDispatchResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<ConversationRecoveryController> CreateAsync(bool pendingPrompt = false)
    {
        var conversation = ConversationFactory.CreateForAgent(ConversationKind.HumanAgent,
            _conversation, _agent, "Recovery", CitizenId.Of(_agent));
        conversation.ActiveSessionId = _session;
        conversation.PendingAskUserJson = pendingPrompt ? "{\"id\":\"preserve-me\"}" : null;
        await _conversations.CreateAsync(conversation);
        var session = await _sessions.GetOrCreateAsync(_session, _agent);
        session.ConversationId = _conversation;
        await _sessions.SaveAsync(session);
        _handle.AgentId.Returns(_agent);
        _handle.SessionId.Returns(_session);
        _supervisor.GetHandle(_agent, _session).Returns(_handle);
        _supervisor.TryReserveMaintenance(_handle).Returns(_runtime);
        _handle.RecoverySafety.Returns("safe");
        _queue.TryReserveIdle(Arg.Any<InboundMessage>()).Returns(_reservation);
        _reservation.TryPost(Arg.Any<InboundMessage>(), Arg.Any<InboundExecutionControl>()).Returns(call =>
        {
            _control = call.Arg<InboundExecutionControl>();
            return true;
        });
        _reservation.Completion.Returns(_completion.Task);
        return new ConversationRecoveryController(_conversations, _sessions, _supervisor, _queue);
    }

    [Fact]
    public async Task Resume_ExactIdleSafeSession_AdmitsOneContinuationWithoutReset()
    {
        var controller = await CreateAsync();
        var result = await controller.Resume("worker", "c-recovery",
            new ConversationResumeRequest("s-recovery", "recover-once", "Inspect saved output and finish the assigned stage.", 0), CancellationToken.None);
        result.ShouldBeOfType<AcceptedResult>();
        _reservation.Received(1).TryPost(Arg.Is<InboundMessage>(m => m.RoutingHints != null &&
            m.RoutingHints.RequestedSessionId == _session && m.RoutingHints.RequestedConversationId == _conversation), Arg.Any<InboundExecutionControl>());
        (await _conversations.GetAsync(_conversation)).ShouldNotBeNull().ActiveSessionId.ShouldBe(_session);
        _runtime.Received(1).Dispose();
    }

    [Fact]
    public async Task Resume_PriorUnknownReceipt_RemainsFencedAfterUnrelatedTranscriptGrowth()
    {
        var controller = await CreateAsync();
        var session = (await _sessions.GetAsync(_session)).ShouldNotBeNull();
        session.Metadata["conversationRecovery"] = new Dictionary<string, object?>
        {
            ["operationId"] = "original", ["status"] = "unknown", ["messageCount"] = 0
        };
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "Unrelated activity" });
        await _sessions.SaveAsync(session);
        (await controller.Resume("worker", "c-recovery", new("s-recovery", "original", "Continue", 1), CancellationToken.None))
            .ShouldBeOfType<ConflictObjectResult>();
        _reservation.DidNotReceive().TryPost(Arg.Any<InboundMessage>(), Arg.Any<InboundExecutionControl>());
    }

    [Fact]
    public async Task Resume_MalformedExactReceipt_CannotBeOverwrittenOrReplayed()
    {
        var controller = await CreateAsync();
        var key = "conversationRecoveryOperation:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("corrupt-op")));
        var session = (await _sessions.GetAsync(_session)).ShouldNotBeNull();
        session.Metadata[key] = "malformed receipt";
        await _sessions.SaveAsync(session);
        (await controller.Resume("worker", "c-recovery", new("s-recovery", "corrupt-op", "Continue", 0), CancellationToken.None))
            .ShouldBeOfType<ConflictObjectResult>();
        _reservation.DidNotReceive().TryPost(Arg.Any<InboundMessage>(), Arg.Any<InboundExecutionControl>());
        (await _sessions.GetAsync(_session)).ShouldNotBeNull().Metadata[key].ShouldBe("malformed receipt");
    }

    [Fact]
    public async Task Resume_ActualStartPersistsStartedReceipt()
    {
        var controller = await CreateAsync();
        (await controller.Resume("worker", "c-recovery", new("s-recovery", "op-start", "Continue", 0), CancellationToken.None))
            .ShouldBeOfType<AcceptedResult>();
        await _control.ShouldNotBeNull().NotifyStartedAsync();
        var status = await controller.Status("worker", "c-recovery", "s-recovery", "op-start", CancellationToken.None);
        var receipt = JsonSerializer.SerializeToElement(status.ShouldBeOfType<OkObjectResult>().Value, JsonSerializerOptions.Web);
        receipt.GetProperty("status").GetString().ShouldBe("processing-started");
        receipt.GetProperty("startedAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Resume_StatusTracksExactOperation_AndStartRefusesReboundSession()
    {
        var controller = await CreateAsync();
        (await controller.Resume("worker", "c-recovery", new("s-recovery", "op-rebind", "Continue", 0), CancellationToken.None))
            .ShouldBeOfType<AcceptedResult>();
        var status = await controller.Status("worker", "c-recovery", "s-recovery", "op-rebind", CancellationToken.None);
        var receipt = JsonSerializer.SerializeToElement(status.ShouldBeOfType<OkObjectResult>().Value, JsonSerializerOptions.Web);
        receipt.GetProperty("status").GetString().ShouldBe("admitted");
        var conversation = (await _conversations.GetAsync(_conversation)).ShouldNotBeNull();
        conversation.ActiveSessionId = SessionId.From("s-rebound");
        await _conversations.SaveAsync(conversation);
        await Should.ThrowAsync<InvalidOperationException>(() => _control.ShouldNotBeNull().NotifyStartedAsync());
    }

    [Fact]
    public async Task Resume_ExactOperationReplay_DoesNotPostAgainEvenAfterCompletedRecord()
    {
        var controller = await CreateAsync();
        var request = new ConversationResumeRequest("s-recovery", "op-once", "Continue", 0);
        (await controller.Resume("worker", "c-recovery", request, CancellationToken.None)).ShouldBeOfType<AcceptedResult>();
        (await controller.Resume("worker", "c-recovery", request, CancellationToken.None)).ShouldBeOfType<OkObjectResult>();
        _reservation.Received(1).TryPost(Arg.Any<InboundMessage>(), Arg.Any<InboundExecutionControl>());
        (await controller.Resume("worker", "c-recovery", request with { Message = "Changed input" }, CancellationToken.None))
            .ShouldBeOfType<ConflictObjectResult>();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("commands-running")]
    [InlineData("commands-unresolved")]
    [InlineData("queued")]
    public async Task Resume_UnsafeRuntime_RefusesWithoutPosting(string safety)
    {
        var controller = await CreateAsync();
        _handle.RecoverySafety.Returns(safety);
        var result = await controller.Resume("worker", "c-recovery", new("s-recovery", "op-unsafe", "Continue", 0), CancellationToken.None);
        result.ShouldBeOfType<ConflictObjectResult>();
        _reservation.DidNotReceive().TryPost(Arg.Any<InboundMessage>(), Arg.Any<InboundExecutionControl>());
    }

    [Fact]
    public async Task Resume_LiveOrQueueBusy_RefusesWithoutInterrupting()
    {
        var controller = await CreateAsync();
        _supervisor.TryReserveMaintenance(_handle).Returns((IDisposable?)null);
        (await controller.Resume("worker", "c-recovery", new("s-recovery", "op-live", "Continue", 0), CancellationToken.None))
            .ShouldBeOfType<ConflictObjectResult>();
        _reservation.DidNotReceive().TryPost(Arg.Any<InboundMessage>(), Arg.Any<InboundExecutionControl>());
    }

    [Fact]
    public async Task Resume_PendingProtectedPrompt_IsPreservedWithoutPosting()
    {
        var controller = await CreateAsync(pendingPrompt: true);
        (await controller.Resume("worker", "c-recovery", new("s-recovery", "op-wait", "Continue", 0), CancellationToken.None))
            .ShouldBeOfType<ConflictObjectResult>();
        (await _conversations.GetAsync(_conversation)).ShouldNotBeNull().PendingAskUserJson.ShouldNotBeNull().ShouldContain("preserve-me");
        _queue.DidNotReceive().TryReserveIdle(Arg.Any<InboundMessage>());
    }

    [Fact]
    public async Task Resume_WrongExpectedSession_RefusesBeforeRuntimeReservation()
    {
        var controller = await CreateAsync();
        (await controller.Resume("worker", "c-recovery", new("s-other", "op-stale", "Continue", 0), CancellationToken.None))
            .ShouldBeOfType<ConflictObjectResult>();
        _supervisor.DidNotReceive().TryReserveMaintenance(Arg.Any<IAgentHandle>());
    }
}
