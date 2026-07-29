using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Issue #2411: <c>GET /api/sessions</c> was unbounded by construction - it asked the store
/// for every session ever recorded and projected the whole set. These tests pin the bounded
/// contract: the controller clamps <c>limit</c>/<c>offset</c> exactly like the existing
/// <c>{sessionId}/history</c> endpoint (default 50, max 200) and pushes the window into
/// <see cref="ISessionStore.ListSummariesAsync"/>, which the SQLite store honours as a real
/// <c>LIMIT</c>/<c>OFFSET</c> rather than materialising the table.
/// </summary>
public sealed class SessionListPaginationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly InMemoryConversationStore _conversations = new();

    public SessionListPaginationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-tests-{Guid.NewGuid():N}.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup; SQLite file locks can linger briefly on Windows.
        }
    }

    private SqliteSessionStore CreateStore()
        => new(_connectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    // Controller: clamping contract. #2532 moved the filter into the store, so the controller now
    // calls ListSummaryPageAsync and passes the clamped window inside a SessionSummaryQuery. These
    // tests assert the same clamping behaviour they always did, read off the new call.

    private static Mock<ISessionStore> StoreCapturing(out Func<SessionSummaryQuery?> observed)
    {
        var store = new Mock<ISessionStore>();
        SessionSummaryQuery? captured = null;
        store.Setup(s => s.ListSummaryPageAsync(It.IsAny<SessionSummaryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<SessionSummaryQuery, CancellationToken>((query, _) => captured = query)
            .ReturnsAsync(SessionSummaryPage.Empty);
        observed = () => captured;
        return store;
    }

    [Fact]
    public async Task List_WhenLimitOmitted_RequestsStoreDefaultOfFifty()
    {
        var store = StoreCapturing(out var observed);
        var controller = new SessionsController(store.Object);

        var result = await controller.List(null, cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        observed()!.Limit.ShouldBe(50, "an omitted limit must default to 50, not to unbounded");
        observed()!.Offset.ShouldBe(0);
    }

    [Fact]
    public async Task List_WhenLimitExceedsMaximum_ClampsToTwoHundred()
    {
        var store = StoreCapturing(out var observed);
        var controller = new SessionsController(store.Object);

        var result = await controller.List(null, limit: 100_000, cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        observed()!.Limit.ShouldBe(200, "limit must clamp to the in-file maximum of 200");
    }

    [Fact]
    public async Task List_PassesExplicitOffsetThroughToStore()
    {
        var store = StoreCapturing(out var observed);
        var controller = new SessionsController(store.Object);

        var result = await controller.List(null, offset: 25, cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        observed()!.Offset.ShouldBe(25);
    }

    [Fact]
    public async Task List_WithNegativeOffset_ReturnsBadRequest_AndNeverTouchesStore()
    {
        var store = new Mock<ISessionStore>(MockBehavior.Strict);
        var controller = new SessionsController(store.Object);

        var result = await controller.List(null, offset: -1, cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        store.Verify(s => s.ListSummaryPageAsync(It.IsAny<SessionSummaryQuery>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a rejected request must not reach the store");
    }

    [Fact]
    public async Task List_WithNonPositiveLimit_ReturnsBadRequest_AndNeverTouchesStore()
    {
        var store = new Mock<ISessionStore>(MockBehavior.Strict);
        var controller = new SessionsController(store.Object);

        var result = await controller.List(null, limit: 0, cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        store.Verify(s => s.ListSummaryPageAsync(It.IsAny<SessionSummaryQuery>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a rejected request must not reach the store");
    }

    [Fact]
    public async Task List_NeverRequestsUnboundedPage()
    {
        var store = StoreCapturing(out var observed);
        var controller = new SessionsController(store.Object);

        _ = await controller.List(null, cancellationToken: CancellationToken.None);

        observed()!.Limit.ShouldNotBeNull(
            "null means the explicit unbounded opt-in; the REST list endpoint must never use it");
    }

    // #2532 AC1: the filter must travel WITH the window, into the store.

    [Fact]
    public async Task List_PushesAgentAndStatusFilterIntoTheStoreQuery()
    {
        var store = StoreCapturing(out var observed);
        var controller = new SessionsController(store.Object);

        _ = await controller.List("agent-a", cancellationToken: CancellationToken.None);

        var query = observed()!;
        query.AgentId.ShouldBe(
            "agent-a",
            "the agent predicate must reach the store so limit/offset address the FILTERED set (#2532 AC1)");
        query.IncludeInactive.ShouldBeFalse(
            "the status predicate must reach the store too, for the same reason");
    }

    [Fact]
    public async Task List_PushesIncludeInactiveIntoTheStoreQuery()
    {
        var store = StoreCapturing(out var observed);
        var controller = new SessionsController(store.Object);

        _ = await controller.List(null, includeInactive: true, cancellationToken: CancellationToken.None);

        observed()!.IncludeInactive.ShouldBeTrue();
    }

    [Fact]
    public async Task List_PushesConversationFilterIntoTheStoreQuery()
    {
        var store = StoreCapturing(out var observed);
        var controller = new SessionsController(store.Object);

        _ = await controller.List("agent-a", conversationId: "c1", cancellationToken: CancellationToken.None);

        observed()!.ConversationIdFilter.ShouldBe("c1", "#2532 AC3: conversation-scoped reads are a store predicate");
    }

    // #2532 AC5: the response must carry an explicit exhaustion signal.

    [Fact]
    public async Task List_ResponseExposesTotalCountAndHasMore()
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.ListSummaryPageAsync(It.IsAny<SessionSummaryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionSummaryPage([], TotalCount: 107, HasMore: true));
        var controller = new SessionsController(store.Object);

        var ok = (await controller.List(null, cancellationToken: CancellationToken.None)) as OkObjectResult;

        var json = System.Text.Json.JsonSerializer.Serialize(ok!.Value);
        json.ShouldContain("\"totalCount\":107");
        json.ShouldContain("\"hasMore\":true");
    }

    // SQLite store: filter-then-window, with a real LIMIT/OFFSET over the filtered set.

    /// <summary>
    /// #2532 AC1, at the store. With sessions for two agents interleaved, a page requested for one
    /// agent must be a page of THAT AGENT'S rows - not a page of the raw table that happens to
    /// contain some of them. This is the store-level statement of the coordinate-space bug.
    /// </summary>
    [Fact]
    public async Task ListSummaryPageAsync_OffsetAddressesTheFilteredSet_NotTheWholeTable()
    {
        var now = DateTimeOffset.UtcNow;
        // Interleave so that any page of the RAW table mixes the two agents.
        for (var i = 0; i < 10; i++)
        {
            await SeedSessionAsync($"a-{i:D2}", now.AddMinutes(-(i * 2)), "agent-a");
            await SeedSessionAsync($"b-{i:D2}", now.AddMinutes(-(i * 2) - 1), "agent-b");
        }

        var store = CreateStore();

        var first = await store.ListSummaryPageAsync(
            new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "agent-a", Limit: 4, Offset: 0));
        first.Items.Select(s => s.SessionId).ShouldBe(new[] { "a-00", "a-01", "a-02", "a-03" });
        first.TotalCount.ShouldBe(10, "the total is the size of the FILTERED set");
        first.HasMore.ShouldBeTrue();

        // Offset 4 must skip four of AGENT-A's rows, not four raw table rows (which would land on
        // a-02 because agent-b's rows are interleaved).
        var second = await store.ListSummaryPageAsync(
            new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "agent-a", Limit: 4, Offset: 4));
        second.Items.Select(s => s.SessionId).ShouldBe(new[] { "a-04", "a-05", "a-06", "a-07" });
        second.HasMore.ShouldBeTrue();

        var last = await store.ListSummaryPageAsync(
            new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "agent-a", Limit: 4, Offset: 8));
        last.Items.Select(s => s.SessionId).ShouldBe(new[] { "a-08", "a-09" });
        last.HasMore.ShouldBeFalse("the filtered set is exhausted");

        last.Items.ShouldAllBe(s => s.AgentId == "agent-a");
    }

    /// <summary>
    /// The whole roster for one agent must be walkable in exactly ceil(N/pageSize) requests, with
    /// no duplicates and no omissions, when the store holds far more rows for other agents.
    /// </summary>
    [Fact]
    public async Task ListSummaryPageAsync_WalkingTheFilteredSet_VisitsEveryRowExactlyOnce()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 7; i++)
            await SeedSessionAsync($"w-{i:D2}", now.AddMinutes(-i), "agent-a");
        for (var i = 0; i < 40; i++)
            await SeedSessionAsync($"noise-{i:D2}", now.AddMinutes(-i), "agent-b");

        var store = CreateStore();

        var seen = new List<string>();
        var requests = 0;
        var hasMore = true;
        while (hasMore)
        {
            var page = await store.ListSummaryPageAsync(
                new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "agent-a", Limit: 3, Offset: seen.Count));
            requests++;
            seen.AddRange(page.Items.Select(s => s.SessionId));
            hasMore = page.HasMore;
            requests.ShouldBeLessThan(20, "the walk must terminate");
        }

        requests.ShouldBe(3, "ceil(7/3) == 3 - the walk must not crawl through agent-b's rows");
        seen.Distinct().Count().ShouldBe(7);
        seen.ShouldBe(Enumerable.Range(0, 7).Select(i => $"w-{i:D2}").ToArray());
    }

    /// <summary>
    /// Status filtering is a store predicate too, so the total and the page both reflect it.
    /// </summary>
    [Fact]
    public async Task ListSummaryPageAsync_ExcludesInactiveByDefault_AndCountsAccordingly()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedSessionAsync("s-active", now.AddMinutes(-1), "agent-a");
        await SeedSessionAsync("s-sealed", now.AddMinutes(-2), "agent-a", SessionStatus.Sealed);
        await SeedSessionAsync("s-expired", now.AddMinutes(-3), "agent-a", SessionStatus.Expired);

        var store = CreateStore();

        var active = await store.ListSummaryPageAsync(
            new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "agent-a", Limit: 50));
        active.Items.Select(s => s.SessionId).ShouldBe(new[] { "s-active" });
        active.TotalCount.ShouldBe(1, "the total must count the FILTERED set, not the table");

        var all = await store.ListSummaryPageAsync(
            new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "agent-a", IncludeInactive: true, Limit: 50));
        all.TotalCount.ShouldBe(3);
        all.Items.Count.ShouldBe(3);
    }

    /// <summary>
    /// The InMemory/File base-store default must expose the identical filter-then-window contract,
    /// so a differently-configured gateway cannot silently page differently.
    /// </summary>
    [Fact]
    public async Task InMemoryStore_ListSummaryPageAsync_AppliesFilterBeforeWindow()
    {
        var store = new InMemorySessionStore();
        foreach (var id in new[] { "m-a1", "m-a2", "m-a3" })
        {
            var session = await store.GetOrCreateAsync(SessionId.From(id), AgentId.From("agent-a"));
            await store.SaveAsync(session);
        }

        foreach (var id in new[] { "m-b1", "m-b2", "m-b3", "m-b4" })
        {
            var session = await store.GetOrCreateAsync(SessionId.From(id), AgentId.From("agent-b"));
            await store.SaveAsync(session);
        }

        var page = await store.ListSummaryPageAsync(
            new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "agent-a", Limit: 2, Offset: 0));

        page.TotalCount.ShouldBe(3, "total counts agent-a's sessions only");
        page.Items.Count.ShouldBe(2);
        page.Items.ShouldAllBe(s => s.AgentId == "agent-a");
        page.HasMore.ShouldBeTrue();

        var tail = await store.ListSummaryPageAsync(
            new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: "agent-a", Limit: 2, Offset: 2));
        tail.Items.Count.ShouldBe(1);
        tail.HasMore.ShouldBeFalse();
    }

    // SQLite store: real LIMIT/OFFSET.

    [Fact]
    public async Task ListSummariesAsync_AppliesLimit_ReturningNewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedSessionAsync("p-oldest", now.AddHours(-3));
        await SeedSessionAsync("p-middle", now.AddHours(-2));
        await SeedSessionAsync("p-newest", now.AddHours(-1));

        var store = CreateStore();
        var page = await store.ListSummariesAsync(now.AddHours(-24), limit: 2, offset: 0);

        page.Count.ShouldBe(2);
        page.Select(s => s.SessionId).ShouldBe(new[] { "p-newest", "p-middle" });
    }

    [Fact]
    public async Task ListSummariesAsync_AppliesOffset_SkippingEarlierRows()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedSessionAsync("p-oldest", now.AddHours(-3));
        await SeedSessionAsync("p-middle", now.AddHours(-2));
        await SeedSessionAsync("p-newest", now.AddHours(-1));

        var store = CreateStore();
        var page = await store.ListSummariesAsync(now.AddHours(-24), limit: 2, offset: 1);

        page.Select(s => s.SessionId).ShouldBe(new[] { "p-middle", "p-oldest" });
    }

    [Fact]
    public async Task ListSummariesAsync_WhenOffsetBeyondEnd_ReturnsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedSessionAsync("p-only", now.AddHours(-1));

        var store = CreateStore();
        var page = await store.ListSummariesAsync(now.AddHours(-24), limit: 50, offset: 500);

        page.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListSummariesAsync_WithNullLimit_ReturnsEverything_AsExplicitOptIn()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedSessionAsync("p-a", now.AddHours(-1));
        await SeedSessionAsync("p-b", now.AddHours(-2));
        await SeedSessionAsync("p-c", now.AddHours(-3));

        var store = CreateStore();
        var all = await store.ListSummariesAsync(now.AddHours(-24), limit: null, offset: 0);

        all.Count.ShouldBe(3, "limit: null is the explicit unbounded opt-in warmup/cron rely on");
    }

    [Fact]
    public async Task ListSummariesAsync_WithNoTimeWindow_BoundsInSql_AndHonoursOffset()
    {
        // DateTimeOffset.MinValue is exactly what the REST list endpoint passes, so this is
        // the path that actually gets the SQL LIMIT/OFFSET. The window-filtered tests above
        // exercise the managed fallback instead; both must expose the same page contract.
        var now = DateTimeOffset.UtcNow;
        await SeedSessionAsync("q-oldest", now.AddHours(-3));
        await SeedSessionAsync("q-middle", now.AddHours(-2));
        await SeedSessionAsync("q-newest", now.AddHours(-1));

        var store = CreateStore();

        var first = await store.ListSummariesAsync(DateTimeOffset.MinValue, limit: 2, offset: 0);
        first.Select(s => s.SessionId).ShouldBe(new[] { "q-newest", "q-middle" });

        var second = await store.ListSummariesAsync(DateTimeOffset.MinValue, limit: 2, offset: 2);
        second.Select(s => s.SessionId).ShouldBe(new[] { "q-oldest" });

        var past = await store.ListSummariesAsync(DateTimeOffset.MinValue, limit: 2, offset: 99);
        past.ShouldBeEmpty();
    }

    // Base store default: same window semantics for File/InMemory.

    [Fact]
    public async Task InMemoryStore_ListSummariesAsync_AppliesLimitAndOffset()
    {
        var store = new InMemorySessionStore();
        foreach (var id in new[] { "m-a", "m-b", "m-c" })
        {
            var session = await store.GetOrCreateAsync(SessionId.From(id), AgentId.From("agent-a"));
            await store.SaveAsync(session);
        }

        var page = await store.ListSummariesAsync(DateTimeOffset.MinValue, limit: 2, offset: 0);
        page.Count.ShouldBe(2);

        var tail = await store.ListSummariesAsync(DateTimeOffset.MinValue, limit: 2, offset: 2);
        tail.Count.ShouldBe(1);

        var all = await store.ListSummariesAsync(DateTimeOffset.MinValue, limit: null, offset: 0);
        all.Count.ShouldBe(3);
    }

    private async Task SeedSessionAsync(
        string sessionId,
        DateTimeOffset updatedAt,
        string agentId = "agent-a",
        SessionStatus status = SessionStatus.Active)
    {
        var conversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From(agentId)
        });

        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From(agentId));
        session.Session.ConversationId = conversationId;
        session.Status = status;
        session.AddEntries(new[]
        {
            new SessionEntry
            {
                Role = MessageRole.FromString("user"),
                Content = "hello",
                Timestamp = updatedAt
            }
        });
        // Set UpdatedAt last: AddEntries bumps it to "now".
        session.UpdatedAt = updatedAt;
        await store.SaveAsync(session);
    }
}
