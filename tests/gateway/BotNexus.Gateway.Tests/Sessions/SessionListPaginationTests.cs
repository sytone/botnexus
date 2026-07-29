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

    // Controller: clamping contract.

    [Fact]
    public async Task List_WhenLimitOmitted_RequestsStoreDefaultOfFifty()
    {
        var store = new Mock<ISessionStore>();
        int? observedLimit = -1;
        var observedOffset = -1;
        store.Setup(s => s.ListSummariesAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, int?, int, CancellationToken>((_, limit, offset, _) =>
            {
                observedLimit = limit;
                observedOffset = offset;
            })
            .ReturnsAsync(Array.Empty<SessionSummary>());
        var controller = new SessionsController(store.Object);

        var result = await controller.List(null, cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        observedLimit.ShouldBe(50, "an omitted limit must default to 50, not to unbounded");
        observedOffset.ShouldBe(0);
    }

    [Fact]
    public async Task List_WhenLimitExceedsMaximum_ClampsToTwoHundred()
    {
        var store = new Mock<ISessionStore>();
        int? observedLimit = -1;
        store.Setup(s => s.ListSummariesAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, int?, int, CancellationToken>((_, limit, _, _) => observedLimit = limit)
            .ReturnsAsync(Array.Empty<SessionSummary>());
        var controller = new SessionsController(store.Object);

        var result = await controller.List(null, limit: 100_000, cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        observedLimit.ShouldBe(200, "limit must clamp to the in-file maximum of 200");
    }

    [Fact]
    public async Task List_PassesExplicitOffsetThroughToStore()
    {
        var store = new Mock<ISessionStore>();
        var observedOffset = -1;
        store.Setup(s => s.ListSummariesAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, int?, int, CancellationToken>((_, _, offset, _) => observedOffset = offset)
            .ReturnsAsync(Array.Empty<SessionSummary>());
        var controller = new SessionsController(store.Object);

        var result = await controller.List(null, offset: 25, cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        observedOffset.ShouldBe(25);
    }

    [Fact]
    public async Task List_WithNegativeOffset_ReturnsBadRequest_AndNeverTouchesStore()
    {
        var store = new Mock<ISessionStore>(MockBehavior.Strict);
        var controller = new SessionsController(store.Object);

        var result = await controller.List(null, offset: -1, cancellationToken: CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        store.Verify(s => s.ListSummariesAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
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
        store.Verify(s => s.ListSummariesAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a rejected request must not reach the store");
    }

    [Fact]
    public async Task List_NeverRequestsUnboundedPage()
    {
        var store = new Mock<ISessionStore>();
        int? observedLimit = null;
        store.Setup(s => s.ListSummariesAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, int?, int, CancellationToken>((_, limit, _, _) => observedLimit = limit)
            .ReturnsAsync(Array.Empty<SessionSummary>());
        var controller = new SessionsController(store.Object);

        _ = await controller.List(null, cancellationToken: CancellationToken.None);

        observedLimit.ShouldNotBeNull(
            "null means the explicit unbounded opt-in; the REST list endpoint must never use it");
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

    private async Task SeedSessionAsync(string sessionId, DateTimeOffset updatedAt)
    {
        var conversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("agent-a")
        });

        var store = CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From("agent-a"));
        session.Session.ConversationId = conversationId;
        session.Status = SessionStatus.Active;
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
