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
