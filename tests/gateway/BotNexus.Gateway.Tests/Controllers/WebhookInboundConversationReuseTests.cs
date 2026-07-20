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
/// Exercises the controller and production SQLite registration store together. Mock-only controller
/// tests cannot detect a stale aggregate write that undoes an earlier atomic store mutation.
/// </summary>
public sealed class WebhookInboundConversationReuseTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteWebhookRegistrationStore _registrations = null!;
    private SqliteWebhookRunStore _runs = null!;
    private InMemoryConversationStore _conversations = null!;
    private IInboundMessageOrchestrator _orchestrator = null!;
    private ISessionStore _sessions = null!;
    private IHttpClientFactory _httpClientFactory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"webhook-inbound-seam-{Guid.NewGuid():N}.db");
        _registrations = new SqliteWebhookRegistrationStore(_dbPath);
        _runs = new SqliteWebhookRunStore(_dbPath);
        await _registrations.InitializeAsync();
        await _runs.InitializeAsync();
        _conversations = new InMemoryConversationStore();
        _orchestrator = Substitute.For<IInboundMessageOrchestrator>();
        _orchestrator.AcceptAsync(Arg.Any<InboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(InboundDispatchResult.NoRoute());
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
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Full-suite parallelism can briefly retain a SQLite handle after pool
                    // clearing. The unique temp path prevents cross-test contamination.
                }
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Receive_SequentialDeliveries_ReusePinnedConversation()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());

        var first = await ReceiveAsync(registration);
        var second = await ReceiveAsync(registration);

        first.ConversationId.ShouldBe(second.ConversationId);
        (await _conversations.ListAsync(registration.AgentId)).Count.ShouldBe(1);
        var persisted = await _registrations.GetAsync(registration.Id);
        persisted.ShouldNotBeNull();
        persisted.PinnedConversationId.ShouldBe(ConversationId.From(first.ConversationId));
    }

    [Fact]
    public async Task Receive_ParallelFirstDeliveries_ArchiveRaceLoserAndShareWinner()
    {
        var registration = await _registrations.CreateAsync(CreateRegistration());
        IConversationStore conversations = new CreateBarrierConversationStore(_conversations);

        var responses = await Task.WhenAll(
            ReceiveAsync(registration, conversations),
            ReceiveAsync(registration, conversations));

        responses.Select(response => response.ConversationId).Distinct().Count().ShouldBe(1);
        var stored = await _conversations.ListAsync(registration.AgentId);
        stored.Count(conversation => conversation.Status == ConversationStatus.Active).ShouldBe(1);
        stored.Count(conversation => conversation.Status == ConversationStatus.Archived).ShouldBe(1);
    }

    private Task<WebhookAcceptedResponse> ReceiveAsync(WebhookRegistration registration) =>
        ReceiveAsync(registration, _conversations);

    private async Task<WebhookAcceptedResponse> ReceiveAsync(
        WebhookRegistration registration,
        IConversationStore conversations)
    {
        var rawBody = Encoding.UTF8.GetBytes("{\"message\":\"Task changed\",\"agentAction\":true}");
        var controller = new WebhookInboundController(
            _registrations,
            _runs,
            _orchestrator,
            conversations,
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

        var result = await controller.Receive(
            registration.AgentId.Value,
            registration.Id.Value,
            CancellationToken.None);

        var accepted = result.ShouldBeOfType<AcceptedResult>();
        return accepted.Value.ShouldBeOfType<WebhookAcceptedResponse>();
    }

    private sealed class CreateBarrierConversationStore(IConversationStore inner) : IConversationStore
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _bothCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _createdCount;

        public async Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
        {
            var created = await inner.CreateAsync(conversation, ct);
            lock (_gate)
            {
                _createdCount++;
                if (_createdCount == 2)
                    _bothCreated.TrySetResult();
            }

            await _bothCreated.Task.WaitAsync(ct);
            return created;
        }

        public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default) => inner.GetAsync(conversationId, ct);
        public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default) => inner.ListAsync(agentId, ct);
        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default) => inner.ListForCitizenAsync(citizen, ct);
        public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default) => inner.AddParticipantsAsync(conversationId, participants, ct);
        public Task SaveAsync(Conversation conversation, CancellationToken ct = default) => inner.SaveAsync(conversation, ct);
        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default) => inner.ArchiveAsync(conversationId, ct);
        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default) => inner.ResolveByBindingAsync(agentId, channelType, channelAddress, ct);
        public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default) => inner.TouchAsync(conversationId, ct);
        public Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default) => inner.PinAsync(conversationId, pin, ct);
        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default) => inner.GetSummariesAsync(ct);
        public Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default) => inner.GetCanvasStateAsync(conversationId, ct);
        public Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default) => inner.SetCanvasStateKeyAsync(conversationId, key, value, ct);
        public Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default) => inner.DeleteCanvasStateKeyAsync(conversationId, key, ct);
        public Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default) => inner.ClearCanvasStateAsync(conversationId, ct);
    }

    private static WebhookRegistration CreateRegistration() => new()
    {
        Id = WebhookId.Create(),
        Label = "TaskNexus task events",
        AgentId = AgentId.From("tinker"),
        Secret = WebhookSecretHelper.GenerateSecret(),
        DefaultResponseMode = WebhookResponseMode.Async,
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
