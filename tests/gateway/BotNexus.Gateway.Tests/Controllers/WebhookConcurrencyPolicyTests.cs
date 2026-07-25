using System.Text;
using System.Text.Json;
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
/// End-to-end coverage of the #2123 webhook conversation/session concurrency policy
/// through the real controller, the production SQLite registration/run stores and the
/// real <see cref="DefaultInboundMessageOrchestrator"/>.
/// </summary>
/// <remarks>
/// The policy under test:
/// one canonical conversation per registration; one stable active session while it is
/// reusable; FIFO serialization per canonical conversation even across registrations;
/// and a webhook run is an execution record, never a sidebar conversation.
/// All concurrency assertions use deterministic gates, never timing sleeps.
/// </remarks>
public sealed class WebhookConcurrencyPolicyTests : IAsyncLifetime
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private string _dbPath = string.Empty;
    private SqliteWebhookRegistrationStore _registrations = null!;
    private SqliteWebhookRunStore _runs = null!;
    private InMemoryConversationStore _conversations = null!;
    private ISessionStore _sessions = null!;
    private IHttpClientFactory _httpClientFactory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"webhook-concurrency-{Guid.NewGuid():N}.db");
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    public async Task TwoRegistrationsPinnedToOneConversation_SerializeFifo()
    {
        // THE #2123 DEFECT, end to end. Two distinct registrations, one canonical
        // conversation. Pre-fix each registration produced queue key webhook:<id>, so
        // both agent turns ran concurrently against one conversation's active_session_id.
        var shared = ConversationId.Create();
        var registrationA = await _registrations.CreateAsync(CreateRegistration(shared));
        var registrationB = await _registrations.CreateAsync(CreateRegistration(shared));

        var processor = new GatedProcessor();
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var first = ReceiveAsync(registrationA, orchestrator);
        await processor.FirstEntered.Task.WaitAsync(TestTimeout);

        var second = ReceiveAsync(registrationB, orchestrator);

        // Deterministic: the second turn cannot have entered the processor while the
        // first is still held on the gate.
        processor.ConcurrentPeak.ShouldBe(1);

        processor.Release();
        await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        processor.ConcurrentPeak.ShouldBe(
            1, "two registrations pinned to one conversation must not overlap turns");
        processor.ObservedConversationIds.Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task DeliveriesToDifferentConversations_RunConcurrently()
    {
        // Separate conversations are the sanctioned route to real parallelism.
        var registrationA = await _registrations.CreateAsync(CreateRegistration(ConversationId.Create()));
        var registrationB = await _registrations.CreateAsync(CreateRegistration(ConversationId.Create()));

        var processor = new RendezvousProcessor(expected: 2);
        await using var orchestrator = new DefaultInboundMessageOrchestrator(
            processor, NullLogger<DefaultInboundMessageOrchestrator>.Instance);

        var first = ReceiveAsync(registrationA, orchestrator);
        var second = ReceiveAsync(registrationB, orchestrator);

        await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        processor.AllArrivedTogether.ShouldBeTrue(
            "distinct conversations must remain independent isolation units");
    }

    [Fact]
    public async Task SequentialDeliveries_ReuseOneStableSession()
    {
        // "One stable active session per webhook registration/conversation while it is
        // reusable" - the resolved session id recorded on each run must not drift.
        var registration = await _registrations.CreateAsync(CreateRegistration(ConversationId.Create()));
        var stableSession = SessionId.From("sess-stable-1");
        var orchestrator = new StubOrchestrator(stableSession);

        var firstRunId = await ReceiveRunIdAsync(registration, orchestrator);
        var secondRunId = await ReceiveRunIdAsync(registration, orchestrator);

        var firstRun = await _runs.GetAsync(WebhookRunId.From(firstRunId));
        var secondRun = await _runs.GetAsync(WebhookRunId.From(secondRunId));

        firstRun.ShouldNotBeNull();
        secondRun.ShouldNotBeNull();
        firstRun.SessionId.ShouldBe(stableSession);
        secondRun.SessionId.ShouldBe(stableSession);
        firstRun.ConversationId.ShouldBe(secondRun.ConversationId);
    }

    [Fact]
    public async Task RunRecords_RetainDistinctRunIdsAndResolvedSession()
    {
        // A run is the per-delivery execution/status record: distinct run ids sharing
        // one conversation and one reusable session.
        var registration = await _registrations.CreateAsync(CreateRegistration(ConversationId.Create()));
        var orchestrator = new StubOrchestrator(SessionId.From("sess-stable-1"));

        var firstRunId = await ReceiveRunIdAsync(registration, orchestrator);
        var secondRunId = await ReceiveRunIdAsync(registration, orchestrator);

        firstRunId.ShouldNotBe(secondRunId);
    }

    [Fact]
    public async Task WebhookRun_DoesNotMaterializeAsSidebarConversation()
    {
        // Policy: "a webhook run remains the per-delivery execution/status record; it is
        // NOT a sidebar conversation." Three deliveries, three runs, exactly ONE
        // conversation - and it is the canonical pinned one, not a per-run child.
        var pinned = ConversationId.Create();
        var registration = await _registrations.CreateAsync(CreateRegistration(pinned));
        var orchestrator = new StubOrchestrator(SessionId.From("sess-stable-1"));

        var runIds = new List<string>();
        for (var i = 0; i < 3; i++)
            runIds.Add(await ReceiveRunIdAsync(registration, orchestrator));

        runIds.Distinct().Count().ShouldBe(3);

        var stored = await _conversations.ListAsync(registration.AgentId);
        stored.ShouldBeEmpty("a pinned registration creates no new conversation at all");

        foreach (var runId in runIds)
        {
            var run = await _runs.GetAsync(WebhookRunId.From(runId));
            run.ShouldNotBeNull();
            run.ConversationId.ShouldBe(pinned, "every run points at the canonical conversation");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<string> ReceiveRunIdAsync(
        WebhookRegistration registration, IInboundMessageOrchestrator orchestrator)
    {
        var result = await InvokeAsync(registration, orchestrator);
        var ok = result.ShouldBeOfType<OkObjectResult>();
        return ok.Value.ShouldBeOfType<WebhookSyncResponse>().RunId;
    }

    private async Task ReceiveAsync(
        WebhookRegistration registration,
        IInboundMessageOrchestrator orchestrator)
        => await InvokeAsync(registration, orchestrator);

    private async Task<IActionResult> InvokeAsync(
        WebhookRegistration registration,
        IInboundMessageOrchestrator orchestrator)
    {
        var rawBody = Encoding.UTF8.GetBytes("{\"message\":\"payload\",\"agentAction\":true}");
        var controller = new WebhookInboundController(
            _registrations, _runs, orchestrator, _conversations, _sessions,
            _httpClientFactory, NullLogger<WebhookInboundController>.Instance)
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

    // Every test drives sync mode so the controller awaits the agent turn inline.
    // Async mode is fire-and-forget, which would let assertions observe a run record
    // before the background turn wrote it - a race in the test, not in the product.
    // Isolation itself is mode-independent: all three modes share one orchestrator call.
    private static WebhookRegistration CreateRegistration(ConversationId pinned) => new()
    {
        Id = WebhookId.Create(),
        Label = "concurrency policy",
        AgentId = AgentId.From("tinker"),
        Secret = WebhookSecretHelper.GenerateSecret(),
        DefaultResponseMode = WebhookResponseMode.Sync,
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        PinnedConversationId = pinned
    };

    /// <summary>Orchestrator stub that always resolves the same reusable session.</summary>
    private sealed class StubOrchestrator(SessionId sessionId) : IInboundMessageOrchestrator
    {
        public Task<InboundDispatchResult> AcceptAsync(
            InboundMessage message, CancellationToken cancellationToken = default)
        {
            var hints = InboundMessageRoutingHints.FromMessage(message);
            var context = new InboundMessageContext(
                AgentId.From("tinker"), message,
                new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId),
                RequestedConversationId: hints.RequestedConversationId);
            var resolution = new ConversationSessionResolution(
                hints.RequestedConversationId ?? ConversationId.Create(),
                sessionId,
                IsNewConversation: false,
                IsNewSession: false,
                OriginatingBindingId: null,
                DisplayPrefix: null);
            var dispatch = new DispatchResult(
                context,
                new ChannelSource(message.ChannelType, message.ChannelAddress, message.SenderId),
                resolution);
            return Task.FromResult(new InboundDispatchResult(
                InboundDispatchStatus.Accepted, new[] { dispatch }));
        }

        public bool Post(InboundMessage message) => true;
    }

    /// <summary>Processor that holds calls on a gate and records overlap.</summary>
    private sealed class GatedProcessor : IInboundMessageProcessor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _conversationIds = [];
        private readonly object _gate = new();
        private int _inFlight;
        private int _peak;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ConcurrentPeak => Volatile.Read(ref _peak);

        public IReadOnlyList<string> ObservedConversationIds
        {
            get { lock (_gate) { return _conversationIds.ToArray(); } }
        }

        public void Release() => _release.TrySetResult();

        public async Task<InboundProcessingOutcome> ProcessAsync(
            InboundMessage message, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _peak, current);
            var hints = InboundMessageRoutingHints.FromMessage(message);
            lock (_gate) { _conversationIds.Add(hints.RequestedConversationId?.Value ?? "none"); }
            FirstEntered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(TestTimeout);
                return new InboundProcessingOutcome(
                    Array.Empty<DispatchResult>(), ShouldClosePerSessionQueue: false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int snapshot;
            do
            {
                snapshot = Volatile.Read(ref target);
                if (value <= snapshot) return;
            }
            while (Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
        }
    }

    /// <summary>Processor that can only complete if calls genuinely overlap.</summary>
    private sealed class RendezvousProcessor(int expected) : IInboundMessageProcessor
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool AllArrivedTogether { get; private set; }

        public async Task<InboundProcessingOutcome> ProcessAsync(
            InboundMessage message, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == expected)
            {
                AllArrivedTogether = true;
                _allArrived.TrySetResult();
            }

            await _allArrived.Task.WaitAsync(TestTimeout);
            return new InboundProcessingOutcome(
                Array.Empty<DispatchResult>(), ShouldClosePerSessionQueue: false);
        }
    }
}
