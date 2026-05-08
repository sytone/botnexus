using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Integration tests verifying that <see cref="AgentInteractionService"/> correctly
/// integrates the <see cref="ConversationHistoryCache"/> based on the
/// <see cref="FeatureFlagsService.ConversationHistoryCache"/> flag.
/// </summary>
public sealed class AgentInteractionServiceCacheTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private const string AgentId = "agent-1";
    private const string ConvId = "conv-1";

    public AgentInteractionServiceCacheTests()
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = AgentId,
            DisplayName = "Agent 1",
            IsConnected = true
        });

        // Seed a conversation that needs history loaded
        _store.GetAgent(AgentId)!.Conversations[ConvId] = new ConversationState
        {
            ConversationId = ConvId,
            Title = "Test conv",
            HistoryLoaded = false
        };
        _store.SetActiveConversation(AgentId, ConvId);
    }

    public void Dispose() => _ctx.Dispose();

    private (AgentInteractionService service, ConversationHistoryCache cache, FeatureFlagsService flags)
        CreateService(bool flagEnabled = true, string? cachedJson = null)
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        // Setup feature flag localStorage
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "bn:feature:conversationHistoryCache")
            .SetResult(flagEnabled ? "true" : null);

        // Setup cache localStorage
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", $"bn:conv-history:{ConvId}")
            .SetResult(cachedJson);

        var js = _ctx.JSInterop.JSRuntime;
        var flags = new FeatureFlagsService(js);
        var cache = new ConversationHistoryCache(js);
        var service = new AgentInteractionService(_store, new GatewayHubConnection(), _restClient, cache, flags);

        return (service, cache, flags);
    }

    private static ConversationHistoryResponseDto EmptyHistory(string convId) =>
        new(convId, 0, 0, 200, []);

    private static ConversationHistoryResponseDto HistoryWith(string convId, params string[] contents)
    {
        var entries = contents.Select(c => new ConversationHistoryEntryDto
        {
            Kind = "message",
            SessionId = "session-test",
            Role = "user",
            Content = c,
            Timestamp = DateTimeOffset.UtcNow
        }).ToList();
        return new(convId, entries.Count, 0, 200, entries);
    }

    private string SerializeCached(params string[] contents)
    {
        var messages = contents.Select(c => new ChatMessage("User", c, DateTimeOffset.UtcNow)).ToList();
        var cached = new CachedHistory(ConvId, DateTimeOffset.UtcNow, messages);
        return System.Text.Json.JsonSerializer.Serialize(cached);
    }

    // ── Flag off ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadHistory_WhenFlagOff_SkipsCache_CallsRestDirectly()
    {
        _restClient.GetHistoryAsync(ConvId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(HistoryWith(ConvId, "server message"));

        var (service, _, flags) = CreateService(flagEnabled: false);
        await flags.InitializeAsync();

        await service.SelectConversationAsync(AgentId, ConvId);

        await _restClient.Received(1).GetHistoryAsync(ConvId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // No cache writes expected
        var setItemCalls = _ctx.JSInterop.Invocations
            .Count(i => i.Identifier == "localStorage.setItem" &&
                        i.Arguments[0]?.ToString()?.StartsWith("bn:conv-history:") == true);
        setItemCalls.ShouldBe(0);
    }

    // ── Flag on, cache miss ───────────────────────────────────────────────

    [Fact]
    public async Task LoadHistory_WhenFlagOn_ChecksCacheFirst()
    {
        _restClient.GetHistoryAsync(ConvId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyHistory(ConvId));

        var (service, _, flags) = CreateService(flagEnabled: true, cachedJson: null);
        await flags.InitializeAsync();

        await service.SelectConversationAsync(AgentId, ConvId);

        // localStorage should have been queried for the cache key
        var getCalls = _ctx.JSInterop.Invocations
            .Count(i => i.Identifier == "localStorage.getItem" &&
                        i.Arguments[0]?.ToString() == $"bn:conv-history:{ConvId}");
        getCalls.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task LoadHistory_WhenCacheMiss_CallsRest_ThenWritesCache()
    {
        _restClient.GetHistoryAsync(ConvId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(HistoryWith(ConvId, "server message"));

        var (service, _, flags) = CreateService(flagEnabled: true, cachedJson: null);
        await flags.InitializeAsync();

        await service.SelectConversationAsync(AgentId, ConvId);

        // REST was called
        await _restClient.Received(1).GetHistoryAsync(ConvId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Cache was written
        var setItemCalls = _ctx.JSInterop.Invocations
            .Count(i => i.Identifier == "localStorage.setItem" &&
                        i.Arguments[0]?.ToString() == $"bn:conv-history:{ConvId}");
        setItemCalls.ShouldBeGreaterThan(0);
    }

    // ── Flag on, cache hit ────────────────────────────────────────────────

    [Fact]
    public async Task LoadHistory_WhenCacheHit_DoesNotCallRest()
    {
        var cachedJson = SerializeCached("cached message");

        // Cache hit but REST never needed for initial render
        _restClient.GetHistoryAsync(ConvId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(EmptyHistory(ConvId));

        var (service, _, flags) = CreateService(flagEnabled: true, cachedJson: cachedJson);
        await flags.InitializeAsync();

        // Reset conversation history state as if not yet loaded
        var conv = _store.GetAgent(AgentId)!.Conversations[ConvId];
        conv.Messages.Clear();

        await service.SelectConversationAsync(AgentId, ConvId);

        // Messages should have been rendered from cache
        conv.Messages.Count.ShouldBeGreaterThan(0);
        conv.Messages.Any(m => m.Content == "cached message").ShouldBeTrue();
    }

    [Fact]
    public async Task LoadHistory_WhenCacheHit_StillRefreshesFromServerInBackground()
    {
        var cachedJson = SerializeCached("cached message");

        _restClient.GetHistoryAsync(ConvId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(HistoryWith(ConvId, "server message"));

        var (service, _, flags) = CreateService(flagEnabled: true, cachedJson: cachedJson);
        await flags.InitializeAsync();

        var conv = _store.GetAgent(AgentId)!.Conversations[ConvId];
        conv.Messages.Clear();

        await service.SelectConversationAsync(AgentId, ConvId);

        // REST was still called to refresh
        await _restClient.Received(1).GetHistoryAsync(ConvId, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Session reset invalidates cache ───────────────────────────────────

    [Fact]
    public async Task ResetSession_InvalidatesCache_ForActiveConversation()
    {
        var agent = _store.GetAgent(AgentId)!;
        agent.SessionId = "session-abc";

        var (service, _, flags) = CreateService(flagEnabled: true);
        await flags.InitializeAsync();

        await service.ResetSessionAsync(AgentId);

        var removeItemCalls = _ctx.JSInterop.Invocations
            .Count(i => i.Identifier == "localStorage.removeItem" &&
                        i.Arguments[0]?.ToString() == $"bn:conv-history:{ConvId}");
        removeItemCalls.ShouldBeGreaterThan(0);
    }
}
