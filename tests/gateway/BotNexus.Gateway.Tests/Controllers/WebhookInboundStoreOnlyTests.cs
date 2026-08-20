using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Controllers;

/// <summary>
/// Covers the <c>agentAction:false</c> store-only webhook path (issue #2839). Before the fix the
/// controller minted <c>SessionId.From(Guid.NewGuid())</c> and appended into a session bound to no
/// conversation, so a 202 was returned for a write that could never be read back. These tests wire
/// the REAL conversation router / dispatcher / session store so the conversation-to-session binding
/// is exercised end to end rather than asserted against a mock's recorded arguments.
/// </summary>
public sealed class WebhookInboundStoreOnlyTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteWebhookRegistrationStore _registrations = null!;
    private SqliteWebhookRunStore _runs = null!;
    private InMemoryConversationStore _conversations = null!;
    private InMemorySessionStore _sessions = null!;
    private IConversationDispatcher _dispatcher = null!;
    private IInboundMessageOrchestrator _orchestrator = null!;
    private IHttpClientFactory _httpClientFactory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"webhook-store-only-{Guid.NewGuid():N}.db");
        _registrations = new SqliteWebhookRegistrationStore(_dbPath);
        _runs = new SqliteWebhookRunStore(_dbPath);
        await _registrations.InitializeAsync();
        await _runs.InitializeAsync();

        _conversations = new InMemoryConversationStore();
        _sessions = new InMemorySessionStore();
        var router = new DefaultConversationRouter(
            _conversations, _sessions, NullLogger<DefaultConversationRouter>.Instance);
        _dispatcher = new DefaultConversationDispatcher(router, _conversations);

        _orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        _orchestrator.AcceptAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(InboundDispatchResult.NoRoute());
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
    }

    public Task DisposeAsync()
    {
        SqlitePoolCleanup.ClearPoolFor(_dbPath);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (!File.Exists(path)) continue;
            try { File.Delete(path); }
            catch (IOException) { /* parallel suite may briefly retain the handle */ }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Acceptance clause 1: the message must be readable from the session bound to the conversation
    /// the caller was handed back, not from an unreachable orphan.
    /// </summary>
    [Fact]
    public async Task StoreOnly_AppendsMessageToConversationBoundSession()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());

        var response = await ReceiveStoreOnlyAsync(registration, "audit trail entry");

        var conversationId = ConversationId.From(response.ConversationId);
        var conversation = await _conversations.GetAsync(conversationId);
        conversation.ShouldNotBeNull();
        conversation.ActiveSessionId.ShouldNotBeNull();

        var boundSession = await _sessions.GetAsync(conversation.ActiveSessionId!.Value);
        boundSession.ShouldNotBeNull();
        boundSession.ConversationId.ShouldBe(conversationId);

        var history = boundSession.GetHistorySnapshot();
        history.Count(e => e.Role == MessageRole.User).ShouldBe(1);
        history.Single(e => e.Role == MessageRole.User).Content.ShouldBe("audit trail entry");
    }

    /// <summary>
    /// Acceptance clause 2: no orphan session. Every session in the store must be bound to the
    /// conversation, and the conversation must own exactly one.
    /// </summary>
    [Fact]
    public async Task StoreOnly_CreatesNoOrphanSession()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());

        var response = await ReceiveStoreOnlyAsync(registration, "first");

        var conversationId = ConversationId.From(response.ConversationId);
        var all = await _sessions.ListAsync();
        all.Count.ShouldBe(1);
        all.ShouldAllBe(s => s.ConversationId == conversationId);

        var forConversation = await _sessions.ListByConversationAsync(conversationId);
        forConversation.Count.ShouldBe(1);
    }

    /// <summary>
    /// Acceptance clause 3: successive store-only posts append to one session rather than minting a
    /// fresh one per delivery.
    /// </summary>
    [Fact]
    public async Task StoreOnly_SuccessivePosts_AppendToSameSession()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());

        var first = await ReceiveStoreOnlyAsync(registration, "one");
        var second = await ReceiveStoreOnlyAsync(registration, "two");

        first.ConversationId.ShouldBe(second.ConversationId);

        var sessions = await _sessions.ListAsync();
        sessions.Count.ShouldBe(1);

        var history = sessions[0].GetHistorySnapshot()
            .Where(e => e.Role == MessageRole.User)
            .Select(e => e.Content)
            .ToList();
        history.ShouldBe(new[] { "one", "two" });
    }

    /// <summary>
    /// Acceptance clause 4 (sad path): when the resolved session cannot receive the append the
    /// endpoint must NOT report 202. A success receipt for a write that did not land is worse than
    /// an error because the caller has no signal to retry.
    /// </summary>
    [Fact]
    public async Task StoreOnly_WhenSessionCannotBeResolved_DoesNotReturnAccepted()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());

        // Dispatcher resolves to a session id that was never created, so the append finds no row.
        var brokenDispatcher = Substitute.For<IConversationDispatcher>();
        brokenDispatcher
            .DispatchAsync(Arg.Any<InboundMessageContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var context = callInfo.Arg<InboundMessageContext>();
                return Task.FromResult(new DispatchResult(
                    context,
                    context.Source,
                    new ConversationSessionResolution(
                        context.RequestedConversationId ?? ConversationId.Create(),
                        SessionId.From("missing-session"),
                        IsNewConversation: false,
                        IsNewSession: false)));
            });

        var result = await ReceiveRawAsync(registration, "will not land", brokenDispatcher);

        result.ShouldNotBeOfType<AcceptedResult>();
        var status = result.ShouldBeAssignableTo<IStatusCodeActionResult>();
        status.StatusCode.ShouldNotBeNull();
        status.StatusCode!.Value.ShouldBeGreaterThanOrEqualTo(400);

        // Nothing was written anywhere — no orphan consolation session.
        (await _sessions.ListAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// Acceptance clause 4 (run record): a store-only delivery that failed to land must not be
    /// recorded as a completed run.
    /// </summary>
    [Fact]
    public async Task StoreOnly_WhenAppendFails_RunIsNotMarkedCompleted()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());

        var brokenDispatcher = Substitute.For<IConversationDispatcher>();
        brokenDispatcher
            .DispatchAsync(Arg.Any<InboundMessageContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var context = callInfo.Arg<InboundMessageContext>();
                return Task.FromResult(new DispatchResult(
                    context,
                    context.Source,
                    new ConversationSessionResolution(
                        context.RequestedConversationId ?? ConversationId.Create(),
                        SessionId.From("missing-session"),
                        IsNewConversation: false,
                        IsNewSession: false)));
            });

        await ReceiveRawAsync(registration, "will not land", brokenDispatcher);

        var runs = await _runs.ListByWebhookAsync(registration.Id);
        runs.ShouldNotBeEmpty();
        runs.ShouldAllBe(r => r.Status != WebhookRunStatus.Completed);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<WebhookAcceptedResponse> ReceiveStoreOnlyAsync(
        WebhookRegistration registration, string message)
    {
        var result = await ReceiveRawAsync(registration, message, _dispatcher);
        var accepted = result.ShouldBeOfType<AcceptedResult>();
        return accepted.Value.ShouldBeOfType<WebhookAcceptedResponse>();
    }

    private async Task<IActionResult> ReceiveRawAsync(
        WebhookRegistration registration, string message, IConversationDispatcher dispatcher)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new { message, agentAction = false });
        var rawBody = Encoding.UTF8.GetBytes(json);

        var controller = new WebhookInboundController(
            _registrations,
            _runs,
            _orchestrator,
            dispatcher,
            _conversations,
            _sessions,
            _httpClientFactory,
            NullLogger<WebhookInboundController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("gateway.test");
        controller.Request.Body = new MemoryStream(rawBody);
        controller.Request.ContentLength = rawBody.Length;
        controller.Request.Headers["X-BotNexus-Signature-256"] =
            WebhookSecretHelper.ComputeSignature(registration.Secret, rawBody);

        return await controller.Receive(
            registration.AgentId.Value,
            registration.Id.Value,
            CancellationToken.None);
    }

    private static WebhookRegistration CreateRegistration() => new()
    {
        Id = WebhookId.Create(),
        Label = "TaskNexus audit feed",
        AgentId = AgentId.From("tinker"),
        Secret = WebhookSecretHelper.GenerateSecret(),
        DefaultResponseMode = WebhookResponseMode.Async,
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
