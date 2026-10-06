using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SqliteSessionMaintenanceProjectionTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("botnexus-session-maintenance-").FullName;
    private readonly InMemoryConversationStore _conversations = new();

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(_directory, "sessions.db"),
        Pooling = false
    }.ToString();

    private SqliteSessionStore CreateStore(IConversationStore? conversations = null) =>
        new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, conversations ?? _conversations);

    private async Task<GatewaySession> SaveSessionAsync(
        SqliteSessionStore store,
        string sessionId,
        string agentId,
        bool crashSentinel,
        params SessionEntry[] entries)
    {
        var agent = AgentId.From(agentId);
        var conversation = new Conversation
        {
            ConversationId = ConversationId.From($"conv-{sessionId}"),
            AgentId = agent
        };
        await _conversations.CreateAsync(conversation);
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = agent,
            ConversationId = conversation.ConversationId,
            UpdatedAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z")
        };
        foreach (var entry in entries)
            session.AddEntry(entry);
        if (crashSentinel)
            session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "sentinel", IsCrashSentinel = true });
        session.UpdatedAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        await store.SaveAsync(session);
        return session;
    }

    [Fact]
    public async Task ListUnresolvedCrashSentinelsAsync_PagesCandidatesAndDoesNotHydrateUnrelatedSessions()
    {
        var conversations = new Mock<IConversationStore>();
        conversations.Setup(store => store.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationId id, CancellationToken _) => new Conversation
            {
                ConversationId = id,
                AgentId = AgentId.From(id.Value.EndsWith("b", StringComparison.Ordinal) ? "agent-b" : "agent-a")
            });
        var store = CreateStore(conversations.Object);
        await SaveSessionAsync(store, "a", "agent-a", crashSentinel: true);
        await SaveSessionAsync(store, "b", "agent-b", crashSentinel: false,
            new SessionEntry { Role = MessageRole.User, Content = "large unrelated transcript" });
        await SaveSessionAsync(store, "c", "agent-a", crashSentinel: true);
        conversations.Invocations.Clear();

        var first = await store.ListUnresolvedCrashSentinelsAsync(1);
        var second = await store.ListUnresolvedCrashSentinelsAsync(1, first.NextCursor);

        first.Sessions.Select(session => session.SessionId.Value).ShouldBe(["a"]);
        first.NextCursor.ShouldBe("a");
        second.Sessions.Select(session => session.SessionId.Value).ShouldBe(["c"]);
        second.NextCursor.ShouldBeNull();
        first.Sessions[0].History.ShouldContain(entry => entry.IsCrashSentinel);
        second.Sessions[0].History.ShouldContain(entry => entry.IsCrashSentinel);
        conversations.Verify(store => store.GetAsync(
            ConversationId.From("conv-b"), It.IsAny<CancellationToken>()), Times.Never,
            "a non-candidate transcript must not be hydrated during the sentinel scan");
    }

    [Fact]
    public async Task ListCleanupPlanAsync_PagesTranscriptFreeRowsWithOwnerCountAndUtf8Bytes()
    {
        var store = CreateStore();
        var first = await SaveSessionAsync(store, "a", "agent-a", crashSentinel: false,
            new SessionEntry { Role = MessageRole.User, Content = "\u00e9", ToolName = "tool", ToolCallId = "call" });
        first.Metadata["kind"] = "cleanup";
        await store.SaveAsync(first);
        await SaveSessionAsync(store, "b", "agent-b", crashSentinel: false,
            new SessionEntry { Role = MessageRole.Assistant, Content = "second" });

        var pageOne = await store.ListCleanupPlanAsync(1, includeBytes: true);
        var pageTwo = await store.ListCleanupPlanAsync(1, includeBytes: true, pageOne.NextCursor);

        var row = pageOne.Rows.ShouldHaveSingleItem();
        row.SessionId.Value.ShouldBe("a");
        row.AgentId.Value.ShouldBe("agent-a");
        row.ConversationId.Value.ShouldBe("conv-a");
        row.MessageCount.ShouldBe(1);
        row.Bytes.ShouldBe(SessionDiskAccounting.Measure(first));
        pageOne.NextCursor.ShouldBe("a");
        pageTwo.Rows.ShouldHaveSingleItem().SessionId.Value.ShouldBe("b");
        pageTwo.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task ListCleanupPlanAsync_WithoutBytes_PreservesMessageCountAndOmitsPayloadAccounting()
    {
        var store = CreateStore();
        await SaveSessionAsync(store, "without-bytes", "agent-a", crashSentinel: false,
            new SessionEntry { Role = MessageRole.User, Content = "payload must not be measured" });

        var row = (await store.ListCleanupPlanAsync(10, includeBytes: false)).Rows.ShouldHaveSingleItem();
        var sql = SqliteSessionStore.BuildCleanupPlanSql(includeBytes: false);

        row.MessageCount.ShouldBe(1);
        row.Bytes.ShouldBe(0);
        sql.ShouldNotContain("length(", Case.Insensitive,
            "disabled disk budgeting must not evaluate payload lengths");
        sql.ShouldNotContain("content", Case.Insensitive,
            "disabled disk budgeting must not read transcript payload columns");
    }

    [Fact]
    public async Task ListCleanupPlanAsync_WithBytes_DoesNotChargeHistoryOverheadForEmptySession()
    {
        var store = CreateStore();
        await SaveSessionAsync(store, "empty", "agent-a", crashSentinel: false);

        var row = (await store.ListCleanupPlanAsync(10, includeBytes: true)).Rows.ShouldHaveSingleItem();

        row.MessageCount.ShouldBe(0);
        row.Bytes.ShouldBe(2,
            "the persisted empty metadata object is two bytes and a missing history row adds no 64-byte entry overhead");
    }

    [Fact]
    public async Task ExpireIfMatchesAsync_PersistsExpiryAndPreservesEarlierExpiry()
    {
        var store = CreateStore();
        var session = await SaveSessionAsync(store, "expire", "agent-a", crashSentinel: false);
        var existingExpiry = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
        session.ExpiresAt = existingExpiry;
        await store.SaveAsync(session);
        var row = (await store.ListCleanupPlanAsync(10, includeBytes: true)).Rows.ShouldHaveSingleItem();
        var mutationTime = DateTimeOffset.Parse("2026-06-03T12:00:00Z");

        var outcome = await store.ExpireIfMatchesAsync(SessionCleanupFence.Capture(row), mutationTime);
        var reloaded = await CreateStore().GetAsync(session.SessionId);

        outcome.ShouldBe(SessionMutationOutcome.Applied);
        reloaded.ShouldNotBeNull();
        reloaded.Status.ShouldBe(SessionStatus.Expired);
        reloaded.UpdatedAt.ShouldBe(mutationTime);
        reloaded.ExpiresAt.ShouldBe(existingExpiry, "cleanup must preserve an expiry already assigned by prior behavior");
    }

    [Fact]
    public async Task CleanupMutations_WithStaleFence_ReturnConflictAndDoNotMutateRowOrHistory()
    {
        var store = CreateStore();
        var session = await SaveSessionAsync(store, "fenced", "agent-a", crashSentinel: false,
            new SessionEntry { Role = MessageRole.User, Content = "keep" });
        var stale = SessionCleanupFence.Capture((await store.ListCleanupPlanAsync(10, includeBytes: true)).Rows.ShouldHaveSingleItem());
        session.UpdatedAt = session.UpdatedAt.AddMinutes(1);
        await store.SaveAsync(session);

        (await store.ExpireIfMatchesAsync(stale, session.UpdatedAt.AddHours(1)))
            .ShouldBe(SessionMutationOutcome.Conflict);
        (await store.DeleteIfMatchesAsync(stale)).ShouldBe(SessionMutationOutcome.Conflict);

        var reloaded = await CreateStore().GetAsync(session.SessionId);
        reloaded.ShouldNotBeNull();
        reloaded.Status.ShouldBe(SessionStatus.Active);
        reloaded.ExpiresAt.ShouldBeNull();
        reloaded.History.ShouldHaveSingleItem().Content.ShouldBe("keep");
    }

    [Fact]
    public async Task ExpireIfMatchesAsync_OnLegacySchema_AddsAndSerializesExpiresAtColumn()
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE sessions (id TEXT PRIMARY KEY, channel_type TEXT, caller_id TEXT, session_type TEXT, participants_json TEXT, status TEXT, metadata TEXT, created_at TEXT, updated_at TEXT, conversation_id TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        var store = CreateStore();
        await SaveSessionAsync(store, "legacy", "agent-a", crashSentinel: false);
        var row = (await store.ListCleanupPlanAsync(10, includeBytes: true)).Rows.ShouldHaveSingleItem();
        var expiry = DateTimeOffset.Parse("2026-06-04T12:00:00Z");
        (await store.ExpireIfMatchesAsync(SessionCleanupFence.Capture(row), expiry))
            .ShouldBe(SessionMutationOutcome.Applied);

        var reloaded = await CreateStore().GetAsync(row.SessionId);
        reloaded.ShouldNotBeNull();
        reloaded.ExpiresAt.ShouldBe(expiry);
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolsUnder(_directory);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
