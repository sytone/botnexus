using System.Net;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #1691: the chat client must open on the most-recent 20 messages and page backwards
/// (offset += 20, prepend) when the user scrolls to the top, stopping once a page returns
/// fewer than 20 rows. The load-more logic lives in BlazorClient.Core so desktop and mobile
/// share one implementation. These tests exercise the shared service directly.
/// </summary>
public sealed class PaginatedHistoryLoadMoreTests
{
    private readonly ClientStateStore _store = new();
    private readonly IGatewayRestClient _restClient = Substitute.For<IGatewayRestClient>();
    private readonly AgentInteractionService _service;

    public PaginatedHistoryLoadMoreTests()
    {
        _service = new AgentInteractionService(
            _store,
            new GatewayHubConnection(),
            _restClient,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentInteractionService>.Instance);
        _store.UpsertAgent(new AgentState { AgentId = "agent-1", DisplayName = "Agent 1", IsConnected = true });
    }

    private static ConversationHistoryResponseDto Page(int count, int startIndex, int offset, int totalCount) =>
        new("conv-1", TotalCount: totalCount, Offset: offset, Limit: AgentInteractionService.DefaultHistoryPageSize,
            Entries: Enumerable.Range(startIndex, count).Select(i => new ConversationHistoryEntryDto
            {
                Kind = "message",
                SessionId = "s1",
                Role = "user",
                Content = $"msg-{i}",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i)
            }).ToList());

    // #2936: an older page whose rows were folded into a compaction summary. The server returns
    // them (they are transcript, not context) with isFolded set.
    private static ConversationHistoryResponseDto FoldedPage(int count, int startIndex, int offset, int totalCount) =>
        new("conv-1", TotalCount: totalCount, Offset: offset, Limit: AgentInteractionService.DefaultHistoryPageSize,
            Entries: Enumerable.Range(startIndex, count).Select(i => new ConversationHistoryEntryDto
            {
                Kind = "message",
                SessionId = "s1",
                Role = "user",
                Content = $"msg-{i}",
                IsFolded = true,
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i)
            }).ToList());

    [Fact]
    public async Task SelectConversation_loads_only_most_recent_20_on_open()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", HistoryLoaded = false };

        _restClient.GetHistoryAsync("conv-1", 20, 0, Arg.Any<CancellationToken>())
            .Returns(Page(count: 20, startIndex: 80, offset: 0, totalCount: 100));

        await _service.SelectConversationAsync("agent-1", "conv-1");

        // Initial open pulls the first 20-row page at offset 0, not the whole transcript.
        await _restClient.Received(1).GetHistoryAsync("conv-1", 20, 0, Arg.Any<CancellationToken>());
        var conv = agent.Conversations["conv-1"];
        Assert.Equal(20, conv.Messages.Count);
        Assert.Equal("msg-80", conv.Messages[0].Content);
        Assert.Equal("msg-99", conv.Messages[^1].Content);
        Assert.True(conv.HasMoreHistory); // full page => more available
    }

    [Fact]
    public async Task LoadMore_fetches_next_offset_and_prepends_older_messages()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", HistoryLoaded = false };

        _restClient.GetHistoryAsync("conv-1", 20, 0, Arg.Any<CancellationToken>())
            .Returns(Page(count: 20, startIndex: 20, offset: 0, totalCount: 40));
        _restClient.GetHistoryAsync("conv-1", 20, 20, Arg.Any<CancellationToken>())
            .Returns(Page(count: 20, startIndex: 0, offset: 20, totalCount: 40));

        await _service.SelectConversationAsync("agent-1", "conv-1");
        var conv = agent.Conversations["conv-1"];
        Assert.Equal(20, conv.Messages.Count);
        Assert.Equal("msg-20", conv.Messages[0].Content);

        var added = await _service.LoadMoreHistoryAsync("agent-1", "conv-1");

        // Offset advanced by 20 and the older page was prepended, preserving newest at the bottom.
        await _restClient.Received(1).GetHistoryAsync("conv-1", 20, 20, Arg.Any<CancellationToken>());
        Assert.Equal(20, added);
        Assert.Equal(40, conv.Messages.Count);
        Assert.Equal("msg-0", conv.Messages[0].Content);   // older prepended on top
        Assert.Equal("msg-39", conv.Messages[^1].Content); // newest still last
    }

    [Fact]
    public async Task LoadMore_stops_when_page_returns_fewer_than_20()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", HistoryLoaded = false };

        _restClient.GetHistoryAsync("conv-1", 20, 0, Arg.Any<CancellationToken>())
            .Returns(Page(count: 20, startIndex: 8, offset: 0, totalCount: 28));
        _restClient.GetHistoryAsync("conv-1", 20, 20, Arg.Any<CancellationToken>())
            .Returns(Page(count: 8, startIndex: 0, offset: 20, totalCount: 28));

        await _service.SelectConversationAsync("agent-1", "conv-1");
        var conv = agent.Conversations["conv-1"];

        await _service.LoadMoreHistoryAsync("agent-1", "conv-1"); // returns 8 < 20
        Assert.Equal(28, conv.Messages.Count);
        Assert.False(conv.HasMoreHistory);

        // A subsequent load-more is a no-op once exhausted.
        var added = await _service.LoadMoreHistoryAsync("agent-1", "conv-1");
        Assert.Equal(0, added);
        await _restClient.Received(1).GetHistoryAsync("conv-1", 20, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadMore_keeps_paging_past_a_compaction_boundary_while_folded_rows_remain()
    {
        // #2936 AC2: before the fix the server never offered folded rows, the client saw a short
        // page and latched HasMoreHistory = false at the compaction boundary. Now the server may
        // return a LARGER-than-page-size block (an expanded folded run), so "short page" is not the
        // exhaustion test -- the server total is. Paging must continue past the boundary.
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", HistoryLoaded = false };

        // Page 1: 20 live rows out of a 320-row transcript (300 of which are folded).
        _restClient.GetHistoryAsync("conv-1", 20, 0, Arg.Any<CancellationToken>())
            .Returns(Page(count: 20, startIndex: 300, offset: 0, totalCount: 320));
        // Page 2: the whole folded run expanded into ONE response, well over the 20-row page size.
        _restClient.GetHistoryAsync("conv-1", 20, 20, Arg.Any<CancellationToken>())
            .Returns(FoldedPage(count: 300, startIndex: 0, offset: 20, totalCount: 320));

        await _service.SelectConversationAsync("agent-1", "conv-1");
        var conv = agent.Conversations["conv-1"];
        Assert.True(conv.HasMoreHistory);

        var added = await _service.LoadMoreHistoryAsync("agent-1", "conv-1");

        Assert.Equal(300, added);
        Assert.Equal(320, conv.Messages.Count);
        Assert.Equal(320, conv.LoadedHistoryRows);
        // Everything the server has is now held locally, so paging correctly stops here.
        Assert.False(conv.HasMoreHistory);
        // The oldest pre-compaction row is reachable and flagged folded for collapsed rendering.
        Assert.Equal("msg-0", conv.Messages[0].Content);
        Assert.True(conv.Messages[0].IsFolded);
        Assert.False(conv.Messages[^1].IsFolded);
    }

    [Fact]
    public async Task LoadMore_does_not_stop_on_a_short_page_when_server_total_says_more_remain()
    {
        // #2936: the short-page heuristic alone would have latched false here and stranded the
        // remaining pre-compaction history, exactly the reported symptom.
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", HistoryLoaded = false };

        _restClient.GetHistoryAsync("conv-1", 20, 0, Arg.Any<CancellationToken>())
            .Returns(Page(count: 20, startIndex: 30, offset: 0, totalCount: 100));
        _restClient.GetHistoryAsync("conv-1", 20, 20, Arg.Any<CancellationToken>())
            .Returns(FoldedPage(count: 5, startIndex: 25, offset: 20, totalCount: 100));

        await _service.SelectConversationAsync("agent-1", "conv-1");
        await _service.LoadMoreHistoryAsync("agent-1", "conv-1");

        var conv = agent.Conversations["conv-1"];
        Assert.Equal(25, conv.LoadedHistoryRows);
        Assert.True(conv.HasMoreHistory); // 25 of 100 held -- there is more, short page notwithstanding
    }

    [Theory]
    [InlineData(20, 100, 20, true)]    // server total is authoritative: more remains
    [InlineData(100, 100, 20, false)]  // everything held
    [InlineData(25, 100, 5, true)]     // short page but total says otherwise (#2936)
    [InlineData(320, 320, 300, false)] // over-sized folded page, transcript exhausted
    [InlineData(20, 0, 20, true)]      // no usable total -> legacy full-page heuristic
    [InlineData(5, 0, 5, false)]       // no usable total -> legacy short-page heuristic
    public void HasMoreHistory_prefers_server_total_and_falls_back_to_page_size(
        int loadedRows, int totalCount, int lastPageCount, bool expected)
        => Assert.Equal(expected, AgentInteractionService.HasMoreHistory(loadedRows, totalCount, lastPageCount));

    [Fact]
    public async Task LoadMore_is_noop_when_no_more_history()
    {
        var agent = _store.GetAgent("agent-1")!;
        agent.Conversations["conv-1"] = new ConversationState { ConversationId = "conv-1", HistoryLoaded = false };

        _restClient.GetHistoryAsync("conv-1", 20, 0, Arg.Any<CancellationToken>())
            .Returns(Page(count: 5, startIndex: 0, offset: 0, totalCount: 5)); // partial first page

        await _service.SelectConversationAsync("agent-1", "conv-1");
        var conv = agent.Conversations["conv-1"];
        Assert.False(conv.HasMoreHistory);

        var added = await _service.LoadMoreHistoryAsync("agent-1", "conv-1");
        Assert.Equal(0, added);
        await _restClient.DidNotReceive().GetHistoryAsync("conv-1", 20, 5, Arg.Any<CancellationToken>());
    }
}
