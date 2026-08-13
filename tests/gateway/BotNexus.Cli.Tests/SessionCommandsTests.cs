using BotNexus.Cli.Commands;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Behaviour pins for <c>botnexus session list|archive|delete</c> (issue #2812).
/// </summary>
/// <remarks>
/// Every assertion inspects the SESSION STORE after the call, not just the exit code (AC4). An exit
/// code proves the command returned; only the store proves what it did. The store used here is the
/// real <see cref="InMemorySessionStore"/>, not a mock, so the commands are exercised against a genuine
/// <see cref="ISessionStore"/> implementation rather than against expectations of one.
/// </remarks>
[Collection("AnsiConsole")]
public class SessionCommandsTests
{
    /// <summary>
    /// A real <see cref="SqliteSessionStore"/> over a throwaway database file. Used for the archive
    /// assertions because the SQLite store is the production implementation and the one that seals the
    /// row in place; the in-memory store drops the row instead, which cannot express "sealed".
    /// </summary>
    private sealed class TempSqliteSessionStore : IDisposable
    {
        private readonly string _directory;

        public TempSqliteSessionStore()
        {
            _directory = Path.Combine(Path.GetTempPath(), "bn-2812-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            var dbPath = Path.Combine(_directory, "sessions.db");
            Store = new SqliteSessionStore(
                $"Data Source={dbPath};Pooling=False",
                NullLogger<SqliteSessionStore>.Instance,
                new InMemoryConversationStore());
        }

        public SqliteSessionStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
        }
    }

    private static async Task<InMemorySessionStore> StoreWithSessionAsync(
        string sessionId,
        string agentId = "farnsworth",
        SessionStatus status = SessionStatus.Active)
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From(agentId));
        session.Status = status;
        await store.SaveAsync(session);
        return store;
    }

    // ── AC1: list reads through the store abstraction ──

    [Fact]
    public async Task List_ReturnsSessionsFromTheStoreAbstraction()
    {
        var store = await StoreWithSessionAsync("s_list_one");
        await store.GetOrCreateAsync(SessionId.From("s_list_two"), AgentId.From("farnsworth"));

        var exitCode = await SessionCommands.ExecuteListAsync(store, agentId: null, limit: 20, format: "json", CancellationToken.None);

        Assert.Equal(0, exitCode);
        var listed = await store.ListAsync();
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task List_DoesNotMutateAnySession()
    {
        var store = await StoreWithSessionAsync("s_list_readonly");
        var before = await store.GetAsync(SessionId.From("s_list_readonly"));
        var beforeUpdatedAt = before!.UpdatedAt;

        await SessionCommands.ExecuteListAsync(store, agentId: null, limit: 20, format: "table", CancellationToken.None);

        var after = await store.GetAsync(SessionId.From("s_list_readonly"));
        Assert.NotNull(after);
        Assert.Equal(SessionStatus.Active, after!.Status);
        Assert.Equal(beforeUpdatedAt, after.UpdatedAt);
    }

    // ── AC2: archive marks the session archived, and is idempotent ──

    [Fact]
    public async Task Archive_SealsTheSessionInTheStore()
    {
        // Sqlite is the production store and seals the row in place, so the sealed status is the
        // observable archive outcome. Asserted through the store, never through console output.
        using var db = new TempSqliteSessionStore();
        var store = db.Store;
        var session = await store.GetOrCreateAsync(SessionId.From("s_archive_target"), AgentId.From("farnsworth"));
        await store.SaveAsync(session);

        var exitCode = await SessionCommands.ExecuteArchiveAsync(store, "s_archive_target", CancellationToken.None);

        Assert.Equal(0, exitCode);
        var reloaded = await store.GetAsync(SessionId.From("s_archive_target"));
        Assert.NotNull(reloaded);
        Assert.Equal(SessionStatus.Sealed, reloaded!.Status);
    }

    [Fact]
    public async Task Archive_IsIdempotent_AlreadyArchivedSessionSucceedsWithoutChangingState()
    {
        using var db = new TempSqliteSessionStore();
        var store = db.Store;
        var session = await store.GetOrCreateAsync(SessionId.From("s_archive_twice"), AgentId.From("farnsworth"));
        await store.SaveAsync(session);

        var first = await SessionCommands.ExecuteArchiveAsync(store, "s_archive_twice", CancellationToken.None);
        Assert.Equal(0, first);

        var afterFirst = await store.GetAsync(SessionId.From("s_archive_twice"));
        Assert.Equal(SessionStatus.Sealed, afterFirst!.Status);
        var sealedAt = afterFirst.UpdatedAt;

        var second = await SessionCommands.ExecuteArchiveAsync(store, "s_archive_twice", CancellationToken.None);

        Assert.Equal(0, second);
        var afterSecond = await store.GetAsync(SessionId.From("s_archive_twice"));
        Assert.NotNull(afterSecond);
        Assert.Equal(SessionStatus.Sealed, afterSecond!.Status);
        // "without changing state" is the load-bearing half of AC2: the second archive must not
        // re-stamp UpdatedAt, which is what a blind re-call to ArchiveAsync would do.
        Assert.Equal(sealedAt, afterSecond.UpdatedAt);
    }

    [Fact]
    public async Task Archive_MissingSession_ReportsNotFoundAndCreatesNothing()
    {
        var store = new InMemorySessionStore();

        var exitCode = await SessionCommands.ExecuteArchiveAsync(store, "s_absent", CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Archive_EmptySelector_IsRefusedAndLeavesStoreUntouched()
    {
        var store = await StoreWithSessionAsync("s_archive_guard");

        var exitCode = await SessionCommands.ExecuteArchiveAsync(store, "   ", CancellationToken.None);

        Assert.Equal(2, exitCode);
        var survivor = await store.GetAsync(SessionId.From("s_archive_guard"));
        Assert.NotNull(survivor);
        Assert.Equal(SessionStatus.Active, survivor!.Status);
    }

    // ── AC3: delete requires an explicit id and refuses ambiguous/empty selectors ──

    [Fact]
    public async Task Delete_RemovesTheSessionFromTheStore()
    {
        var store = await StoreWithSessionAsync("s_delete_target");

        var exitCode = await SessionCommands.ExecuteDeleteAsync(store, "s_delete_target", CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Null(await store.GetAsync(SessionId.From("s_delete_target")));
        Assert.Empty(await store.ListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("s_*")]
    [InlineData("s_?")]
    [InlineData("s_%")]
    [InlineData("s_a,s_b")]
    [InlineData("s_a s_b")]
    public async Task Delete_RefusesAmbiguousOrEmptySelector_AndDeletesNothing(string selector)
    {
        var store = await StoreWithSessionAsync("s_a");
        var second = await store.GetOrCreateAsync(SessionId.From("s_b"), AgentId.From("farnsworth"));
        await store.SaveAsync(second);

        var exitCode = await SessionCommands.ExecuteDeleteAsync(store, selector, CancellationToken.None);

        Assert.Equal(2, exitCode);
        // The refusal is only meaningful if nothing was removed - assert the store, not the code.
        var survivors = await store.ListAsync();
        Assert.Equal(2, survivors.Count);
        Assert.NotNull(await store.GetAsync(SessionId.From("s_a")));
        Assert.NotNull(await store.GetAsync(SessionId.From("s_b")));
    }

    [Fact]
    public async Task Delete_MissingSession_ReportsNotFoundAndLeavesOtherSessionsIntact()
    {
        var store = await StoreWithSessionAsync("s_keep");

        var exitCode = await SessionCommands.ExecuteDeleteAsync(store, "s_absent", CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.NotNull(await store.GetAsync(SessionId.From("s_keep")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("s_*")]
    [InlineData("a b")]
    public void ValidateExplicitId_RejectsEmptyAndAmbiguousSelectors(string? selector)
        => Assert.NotNull(SessionCommands.ValidateExplicitId(selector));

    [Fact]
    public void ValidateExplicitId_AcceptsAnExactId()
        => Assert.Null(SessionCommands.ValidateExplicitId("s_1a2b3c"));
}
