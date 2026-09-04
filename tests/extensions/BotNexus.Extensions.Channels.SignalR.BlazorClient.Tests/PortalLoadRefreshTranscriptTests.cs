using System.Net;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3846: the refresh action must re-fetch the ACTIVE conversation's transcript, not just the
/// agent/conversation rosters. Before this, tapping refresh on a conversation missing messages
/// dropped by SignalR reloaded everything except the thing that was wrong.
/// </summary>
/// <remarks>
/// The hub in these tests is a real <see cref="GatewayHubConnection"/> that is never connected, so
/// every <c>RefreshAsync</c> here takes the re-dial branch and that re-dial FAILS. That is not an
/// accident of the harness - it is the clause-5 condition under test: a failed hub re-dial must not
/// cost the user the REST refresh they asked for (#2541), and the transcript half inherits that
/// same independence.
/// </remarks>
public sealed class PortalLoadRefreshTranscriptTests
{
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly IGatewayEventHandler _eventHandler = Substitute.For<IGatewayEventHandler>();
    private readonly GatewayHubConnection _hub = new();
    private readonly PortalLoadService _service;

    public PortalLoadRefreshTranscriptTests()
    {
        _service = new PortalLoadService(_restClient, _hub, _store, _eventHandler);
    }

    private static ConversationSummaryDto Conv(string id) =>
        new(id, "agent-1", "Chat", true, "Active", "s-1", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static ConversationHistoryEntryDto Entry(string content, int minute, string role = "user") => new()
    {
        Kind = "message",
        SessionId = "s-1",
        Role = role,
        Content = content,
        Timestamp = new DateTimeOffset(2026, 9, 4, 10, minute, 0, TimeSpan.Zero)
    };

    private void ArrangeRoster(params string[] conversationIds)
    {
        _restClient.GetAgentsAsync(Arg.Any<CancellationToken>())
            .Returns([new AgentSummary("agent-1", "Test Agent")]);
        _restClient.GetConversationsAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(conversationIds.Select(Conv).ToList());
        _restClient.GetSessionsAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionPageDto([], 0, false));
    }

    private async Task InitializeAsync(string? activeConversationId)
    {
        _restClient.GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationHistoryResponseDto("conv-1", 0, 0, 200, []));

        await _service.InitializeAsync("http://localhost:5000/hub/gateway");

        if (activeConversationId is not null)
            _store.SetActiveConversation("agent-1", activeConversationId);

        _restClient.ClearReceivedCalls();
    }

    /// <summary>
    /// Clause 1: tapping refresh on an active conversation issues a <c>GetHistoryAsync</c> request
    /// for THAT conversation. This is the whole defect in one assertion.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ActiveConversation_RefetchesItsTranscript()
    {
        ArrangeRoster("conv-1");
        await InitializeAsync("conv-1");

        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationHistoryResponseDto("conv-1", 1, 0, 200, [Entry("hello", 1)]));

        await _service.RefreshAsync();

        await _restClient.Received(1).GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Clause 5 (the #2541 independence rule, extended). The hub is not connected, so the re-dial
    /// branch runs and throws. The transcript re-fetch must still have happened: a failed hub
    /// re-dial must not cost the user the REST refresh they asked for.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_TranscriptFetchSurvivesAFailedHubRedial()
    {
        ArrangeRoster("conv-1");
        await InitializeAsync("conv-1");

        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationHistoryResponseDto("conv-1", 1, 0, 200, [Entry("restored", 2)]));

        _hub.IsConnected.ShouldBeFalse("the re-dial branch must be the one under test");

        await _service.RefreshAsync();

        // The REST transcript fetch happened despite the re-dial that follows it failing, and the
        // fetched row landed in the store.
        await _restClient.Received(1).GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _store.GetConversation("conv-1")!.Messages.Select(m => m.Content).ShouldContain("restored");
    }

    /// <summary>
    /// Clause 5, the other direction: a THROWING transcript fetch must not abort the roster
    /// refresh. Each half is independently guarded.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_FailedTranscriptFetchDoesNotAbortTheRosterRefresh()
    {
        ArrangeRoster("conv-1");
        await InitializeAsync("conv-1");

        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ConversationHistoryResponseDto?>(_ =>
                throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError));

        await _service.RefreshAsync();

        // Roster halves still ran.
        await _restClient.Received(1).GetAgentsAsync(Arg.Any<CancellationToken>());
        await _restClient.Received(1).GetConversationsAsync("agent-1", Arg.Any<CancellationToken>());
        _store.GetAgent("agent-1").ShouldNotBeNull();
    }

    /// <summary>
    /// Clause 4: refresh is safe when no conversation is active - no exception, and the roster
    /// refresh still runs. This is the clause the non-vacuity check (clause 8) requires to stay
    /// GREEN when the history re-fetch is severed, proving the two halves are wired separately.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_NoActiveConversation_StillRefreshesTheRosterAndDoesNotFetchHistory()
    {
        ArrangeRoster();
        await InitializeAsync(activeConversationId: null);

        await _service.RefreshAsync();

        await _restClient.Received(1).GetAgentsAsync(Arg.Any<CancellationToken>());
        await _restClient.DidNotReceive().GetHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Clause 2 at the service level: refreshing a transcript the client already holds in full
    /// changes nothing - same count, same order, no duplicates.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_CompleteTranscript_ProducesNoDuplicates()
    {
        ArrangeRoster("conv-1");

        var page = new ConversationHistoryResponseDto("conv-1", 2, 0, 200, [Entry("one", 1), Entry("two", 2, "assistant")]);
        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(page);

        await _service.InitializeAsync("http://localhost:5000/hub/gateway");
        _store.SetActiveConversation("agent-1", "conv-1");

        var before = _store.GetConversation("conv-1")!.Messages.Select(m => m.Content).ToList();
        before.ShouldBe(["one", "two"]);

        await _service.RefreshAsync();

        _store.GetConversation("conv-1")!.Messages.Select(m => m.Content).ShouldBe(["one", "two"]);
    }

    /// <summary>
    /// Clause 3 at the service level: a row missing from the MIDDLE of the displayed transcript is
    /// restored in its correct chronological position by a refresh.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_RestoresAMessageDroppedFromTheMiddle()
    {
        ArrangeRoster("conv-1");

        // Initial load sees a transcript with a hole where message "two" should be.
        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationHistoryResponseDto("conv-1", 2, 0, 200, [Entry("one", 1), Entry("three", 3)]));

        await _service.InitializeAsync("http://localhost:5000/hub/gateway");
        _store.SetActiveConversation("agent-1", "conv-1");
        _store.GetConversation("conv-1")!.Messages.Select(m => m.Content).ShouldBe(["one", "three"]);

        // The server now returns the complete transcript.
        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationHistoryResponseDto("conv-1", 3, 0, 200,
                [Entry("one", 1), Entry("two", 2, "assistant"), Entry("three", 3)]));

        await _service.RefreshAsync();

        _store.GetConversation("conv-1")!.Messages.Select(m => m.Content).ShouldBe(["one", "two", "three"]);
    }

    /// <summary>
    /// A 404 on the transcript re-fetch (conversation archived/deleted concurrently) is swallowed
    /// exactly as the initial-load path swallows it, and the roster refresh completes.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_TranscriptFetch404_IsNonFatal()
    {
        ArrangeRoster("conv-1");
        await InitializeAsync("conv-1");

        _restClient.GetHistoryAsync("conv-1", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<ConversationHistoryResponseDto?>(_ =>
                throw new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await _service.RefreshAsync();

        await _restClient.Received(1).GetAgentsAsync(Arg.Any<CancellationToken>());
    }
}
