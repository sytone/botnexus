using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Controllers;

/// <summary>
/// Controller-level coverage of the #3851 webhook inbound queue, asserting on OBSERVABLE run state
/// read back from the production SQLite run store rather than on any internal flag.
/// </summary>
/// <remarks>
/// The reported defect was precisely that internal state and observable state disagreed: a delivery
/// blocked on the session write lock reported <see cref="WebhookRunStatus.Running"/>, so the only
/// evidence a caller could obtain said the agent was working when it had not started. A test that
/// asserted on the queue object would therefore prove nothing about the defect - every assertion
/// here goes through the run store or the HTTP result.
/// </remarks>
public sealed class WebhookInboundBackpressureTests : IAsyncLifetime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private string _dbPath = string.Empty;
    private SqliteWebhookRegistrationStore _registrations = null!;
    private SqliteWebhookRunStore _runs = null!;
    private InMemoryConversationStore _conversations = null!;
    private ISessionStore _sessions = null!;
    private IHttpClientFactory _httpClientFactory = null!;

    /// <summary>Registration under test, captured so run lookups can be scoped to it.</summary>
    private WebhookId _webhookId;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"webhook-backpressure-{Guid.NewGuid():N}.db");
        _registrations = new SqliteWebhookRegistrationStore(_dbPath);
        _runs = new SqliteWebhookRunStore(_dbPath);
        await _registrations.InitializeAsync();
        await _runs.InitializeAsync();
        _conversations = new InMemoryConversationStore();
        _sessions = Substitute.For<ISessionStore>();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
    }

    public Task DisposeAsync()
    {
        SqlitePoolCleanup.ClearPoolFor(_dbPath);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (IOException) { /* parallel suites can briefly retain a handle */ }
            }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DeliveryBlockedBehindAnotherTurn_ReportsQueued_NotRunning()
    {
        // AC2, the heart of the issue. Delivery one holds the agent's slot; delivery two cannot
        // start. Its OBSERVABLE run status must say Queued, because pre-fix it said Running and a
        // caller had no way to tell "waiting for the agent" from "the agent is working".
        var registration = await _registrations.CreateAsync(CreateRegistration());
        _webhookId = registration.Id;
        var queue = CreateQueue(depth: 8);
        var orchestrator = new GatedOrchestrator();

        var first = InvokeAsync(registration, orchestrator, queue);
        await orchestrator.FirstEntered.Task.WaitAsync(TestTimeout);

        var second = InvokeAsync(registration, orchestrator, queue);

        // Deterministic: the second delivery is admitted to the queue synchronously, so once the
        // depth is visible its run row has already been written.
        await WaitForWaitingCountAsync(queue, registration.AgentId, 1);
        var queuedRun = await WaitForStatusAsync(WebhookRunStatus.Queued);

        queuedRun.Status.ShouldBe(WebhookRunStatus.Queued);
        queuedRun.StartedAt.ShouldBeNull("a queued delivery has not begun executing");

        orchestrator.Release();
        await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        var all = await _runs.ListByWebhookAsync(registration.Id, 10);
        all.Count().ShouldBe(2);
        all.ShouldAllBe(r => r.Status == WebhookRunStatus.Completed);
    }

    [Fact]
    public async Task DeliveryBeyondTheBound_IsRefusedWith503_NotAccepted()
    {
        // AC4: overload is shed explicitly. Pre-fix every one of these got a 202 receipt for work
        // that might never be serviced.
        var registration = await _registrations.CreateAsync(CreateRegistration());
        _webhookId = registration.Id;
        var queue = CreateQueue(depth: 1);
        var orchestrator = new GatedOrchestrator();

        var holder = InvokeAsync(registration, orchestrator, queue);
        await orchestrator.FirstEntered.Task.WaitAsync(TestTimeout);

        var waiter = InvokeAsync(registration, orchestrator, queue);
        await WaitForWaitingCountAsync(queue, registration.AgentId, 1);

        // The bound is now fully subscribed - this third delivery must be refused outright.
        var refused = await InvokeOnceAsync(registration, orchestrator, queue);

        var status = refused.ShouldBeOfType<ObjectResult>();
        status.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);

        var rejectedRun = await WaitForStatusAsync(WebhookRunStatus.Rejected);
        rejectedRun.Error.ShouldNotBeNullOrWhiteSpace(
            "a refused delivery must record why, so the caller can distinguish it from a failure");
        rejectedRun.StartedAt.ShouldBeNull("a refused delivery never started");

        orchestrator.Release();
        await Task.WhenAll(holder, waiter).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task UncontendedDelivery_NeverPassesThroughQueued()
    {
        // The queued state must carry information: a state set on every run would be no better than
        // the Running-for-everything it replaces.
        var registration = await _registrations.CreateAsync(CreateRegistration());
        _webhookId = registration.Id;
        var queue = CreateQueue(depth: 8);
        var observed = new List<WebhookRunStatus>();
        var orchestrator = new StatusRecordingOrchestrator(_runs, registration.Id, observed);

        await InvokeOnceAsync(registration, orchestrator, queue);

        observed.ShouldNotContain(
            WebhookRunStatus.Queued,
            "a delivery that took the slot immediately never waited for it");
    }

    [Fact]
    public async Task BacklogDepthIsObservable_WithoutReadingRunRows()
    {
        // AC5: a growing backlog is diagnosable from the queue's own depth signal.
        var registration = await _registrations.CreateAsync(CreateRegistration());
        var queue = CreateQueue(depth: 8);
        var orchestrator = new GatedOrchestrator();
        var peak = 0;
        queue.WaitingCountChanged += (_, waiting) => Interlocked.Exchange(ref peak, Math.Max(peak, waiting));

        var holder = InvokeAsync(registration, orchestrator, queue);
        await orchestrator.FirstEntered.Task.WaitAsync(TestTimeout);

        var waiters = new[]
        {
            InvokeAsync(registration, orchestrator, queue),
            InvokeAsync(registration, orchestrator, queue)
        };
        await WaitForWaitingCountAsync(queue, registration.AgentId, 2);

        peak.ShouldBeGreaterThanOrEqualTo(2, "the depth signal must report the backlog forming");

        orchestrator.Release();
        await Task.WhenAll(waiters.Append(holder)).WaitAsync(TestTimeout);
        queue.WaitingCount(registration.AgentId).ShouldBe(0, "the depth signal must also drain");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static WebhookInboundQueue CreateQueue(int depth) =>
        new(new WebhookInboundQueueOptions { MaxQueueDepth = depth });

    private static Task WaitForWaitingCountAsync(
        WebhookInboundQueue queue, AgentId agentId, int expected)
        => TestAwait.EventuallyAsync(
            () => queue.WaitingCount(agentId) >= expected,
            $"the inbound queue depth for '{agentId.Value}' reaches {expected}",
            TestTimeout);

    private async Task<WebhookRun> WaitForStatusAsync(WebhookRunStatus status)
    {
        WebhookRun? match = null;
        await TestAwait.EventuallyAsync(
            async () =>
            {
                var runs = await _runs.ListByWebhookAsync(_webhookId, 200);
                match = runs.FirstOrDefault(r => r.Status == status);
                return match is not null;
            },
            $"a webhook run reaches {status}",
            TestTimeout);
        return match!;
    }

    private async Task InvokeAsync(
        WebhookRegistration registration,
        IInboundMessageOrchestrator orchestrator,
        WebhookInboundQueue queue)
        => await InvokeOnceAsync(registration, orchestrator, queue);

    private async Task<IActionResult> InvokeOnceAsync(
        WebhookRegistration registration,
        IInboundMessageOrchestrator orchestrator,
        WebhookInboundQueue queue)
    {
        var rawBody = Encoding.UTF8.GetBytes("{\"message\":\"payload\",\"agentAction\":true}");
        var controller = new WebhookInboundController(
            _registrations, _runs, orchestrator,
            Substitute.For<IConversationDispatcher>(),
            _conversations, _sessions,
            _httpClientFactory, NullLogger<WebhookInboundController>.Instance,
            bodyGuard: null, inboundQueue: queue, applicationLifetime: null)
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
            registration.AgentId.Value, registration.Id.Value, CancellationToken.None);
    }

    // Sync mode so the controller awaits the turn inline: every assertion then observes a run row
    // the request itself wrote, never one a background task might still be racing to write.
    private static WebhookRegistration CreateRegistration() => new()
    {
        Id = WebhookId.Create(),
        Label = "backpressure",
        AgentId = AgentId.From("tinker"),
        Secret = WebhookSecretHelper.GenerateSecret(),
        DefaultResponseMode = WebhookResponseMode.Sync,
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        PinnedConversationId = ConversationId.Create()
    };

    /// <summary>Orchestrator that holds every call on a gate until released.</summary>
    private sealed class GatedOrchestrator : IInboundMessageOrchestrator
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task<InboundDispatchResult> AcceptAsync(
            InboundMessage message, CancellationToken cancellationToken = default)
        {
            FirstEntered.TrySetResult();
            await _release.Task.WaitAsync(TestTimeout);
            return Resolve(message);
        }

        public bool Post(InboundMessage message) => true;
    }

    /// <summary>Orchestrator that snapshots the run's observable status mid-turn.</summary>
    private sealed class StatusRecordingOrchestrator(
        SqliteWebhookRunStore runs, WebhookId webhookId, List<WebhookRunStatus> observed)
        : IInboundMessageOrchestrator
    {
        public async Task<InboundDispatchResult> AcceptAsync(
            InboundMessage message, CancellationToken cancellationToken = default)
        {
            foreach (var run in await runs.ListByWebhookAsync(webhookId, 200))
                observed.Add(run.Status);
            return Resolve(message);
        }

        public bool Post(InboundMessage message) => true;
    }

    private static InboundDispatchResult Resolve(InboundMessage message)
    {
        var hints = InboundMessageRoutingHints.FromMessage(message);
        var context = new InboundMessageContext(
            AgentId.From("tinker"), message,
            new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId),
            RequestedConversationId: hints.RequestedConversationId);
        var resolution = new ConversationSessionResolution(
            hints.RequestedConversationId ?? ConversationId.Create(),
            SessionId.From("sess-stable-1"),
            IsNewConversation: false,
            IsNewSession: false,
            OriginatingBindingId: null,
            DisplayPrefix: null);
        var dispatch = new DispatchResult(
            context,
            new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId),
            resolution);
        return new InboundDispatchResult(InboundDispatchStatus.Accepted, new[] { dispatch });
    }
}
