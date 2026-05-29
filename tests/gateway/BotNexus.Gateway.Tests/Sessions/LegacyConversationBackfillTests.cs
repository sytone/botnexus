using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Phase 9 / P9-B-1 (#615): save-time + load-time legacy-conversation backfill on
/// all three concrete <see cref="ISessionStore"/> implementations.
///
/// Each store must:
/// <list type="number">
///   <item>Stamp orphan sessions (null <c>ConversationId</c>) with the agent's
///         <c>legacy:{agentId}</c> conversation when <c>SaveAsync</c> is called.</item>
///   <item>For durable stores (SQLite, File): backfill orphan rows loaded from disk
///         so subsequent indexed queries observe the stamp without re-resolution.</item>
///   <item>Preserve back-compat for stores configured without an
///         <see cref="IConversationStore"/> (older test composition roots).</item>
/// </list>
/// </summary>
public sealed class LegacyConversationBackfillTests
{
    // --- InMemorySessionStore (save-time only; no persisted-orphan path) ---

    [Fact]
    public async Task InMemory_SaveAsync_NullConversationId_StampsLegacy_WhenResolverConfigured()
    {
        var conversations = new InMemoryConversationStore();
        var store = new InMemorySessionStore(
            redactor: null,
            conversationStore: conversations,
            logger: NullLogger<InMemorySessionStore>.Instance);
        var agentId = AgentId.From("agent-in-memory");
        var sessionId = SessionId.From("s-in-memory-orphan");

        var session = await store.GetOrCreateAsync(sessionId, agentId);
        session.ConversationId.ShouldBeNull();
        await store.SaveAsync(session);

        session.ConversationId.ShouldNotBeNull();
        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        session.ConversationId!.Value.ShouldBe(legacy.ConversationId);
    }

    [Fact]
    public async Task InMemory_SaveAsync_WithExistingConversationId_DoesNotStamp()
    {
        // Backfill must not overwrite an already-bound conversation — Phase 9's W-1
        // invariant is that legacy stamping is the fallback, not the override.
        var conversations = new InMemoryConversationStore();
        var store = new InMemorySessionStore(redactor: null, conversationStore: conversations, logger: null);
        var agentId = AgentId.From("agent-bound");
        var bound = ConversationId.Create();

        var session = await store.GetOrCreateAsync(SessionId.From("s-bound"), agentId);
        session.ConversationId = bound;
        await store.SaveAsync(session);

        session.ConversationId!.Value.ShouldBe(bound);
        var legacies = (await conversations.ListAsync(agentId))
            .Where(c => c.Title == $"legacy:{agentId.Value}")
            .ToList();
        legacies.ShouldBeEmpty(
            "Legacy resolver must not be invoked for an already-bound session — " +
            "stamping is a backfill, not a default-override.");
    }

    [Fact]
    public async Task InMemory_SaveAsync_NoResolver_PreservesNullConversationId()
    {
        // Back-compat for test composition roots that don't wire IConversationStore.
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-no-resolver"), AgentId.From("a"));
        await store.SaveAsync(session);

        session.ConversationId.ShouldBeNull();
    }

    [Fact]
    public async Task InMemory_SaveAsync_ConcurrentSavesSameAgent_AllStampedSameLegacyConversation()
    {
        // The resolver's per-agent SemaphoreSlim guarantees that 10 concurrent orphan
        // saves from the same agent all converge on a single legacy conversation row.
        var conversations = new InMemoryConversationStore();
        var store = new InMemorySessionStore(redactor: null, conversationStore: conversations, logger: null);
        var agentId = AgentId.From("agent-concurrent-save");

        var sessions = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            var s = await store.GetOrCreateAsync(SessionId.From($"s-conc-{i}"), agentId);
            return s;
        }));
        await Task.WhenAll(sessions.Select(s => store.SaveAsync(s)));

        var stampedIds = sessions.Select(s => s.ConversationId!.Value).Distinct().ToList();
        stampedIds.Count.ShouldBe(1);
        (await conversations.ListAsync(agentId))
            .Count(c => c.Title == $"legacy:{agentId.Value}")
            .ShouldBe(1);
    }

    // --- FileSessionStore (save-time + load-time + sidecar rewrite) ---

    [Fact]
    public async Task File_SaveAsync_NullConversationId_StampsLegacy_AndPersistsToSidecar()
    {
        using var fixture = new FileFixture();
        var conversations = new InMemoryConversationStore();
        var store = fixture.CreateStore(conversations);
        var agentId = AgentId.From("agent-file-orphan");

        var session = await store.GetOrCreateAsync(SessionId.From("s-file-orphan"), agentId);
        await store.SaveAsync(session);

        session.ConversationId.ShouldNotBeNull();

        // Reload from a fresh store instance against the same disk fixture — the stamp
        // must survive without re-invoking the resolver.
        var reloadStore = fixture.CreateStore(conversations);
        var reloaded = await reloadStore.GetAsync(SessionId.From("s-file-orphan"));
        reloaded.ShouldNotBeNull();
        reloaded!.ConversationId.ShouldBe(session.ConversationId);
    }

    [Fact]
    public async Task File_LoadFromFile_OrphanSidecar_BackfillsAndRewritesSidecar()
    {
        // Simulate a pre-Phase-9 sidecar: save the session with NO conversation store
        // (so no stamp), then reload with a resolver and confirm the sidecar gets
        // durably rewritten with the legacy conversation id.
        using var fixture = new FileFixture();
        var conversations = new InMemoryConversationStore();
        var agentId = AgentId.From("agent-orphan-sidecar");

        var preStore = fixture.CreateStore(conversationStore: null);
        var prePhase9 = await preStore.GetOrCreateAsync(SessionId.From("s-orphan-side"), agentId);
        await preStore.SaveAsync(prePhase9);
        prePhase9.ConversationId.ShouldBeNull();

        // Now load through a Phase-9-aware store; the load path must backfill.
        var loadStore = fixture.CreateStore(conversations);
        var loaded = await loadStore.GetAsync(SessionId.From("s-orphan-side"));
        loaded.ShouldNotBeNull();
        loaded!.ConversationId.ShouldNotBeNull();
        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        loaded.ConversationId!.Value.ShouldBe(legacy.ConversationId);

        // A third store instance (no cache; no resolver) must still see the durable stamp
        // — proves the load path rewrote the sidecar, not just the in-memory projection.
        var verifyStore = fixture.CreateStore(conversationStore: null);
        var reverified = await verifyStore.GetAsync(SessionId.From("s-orphan-side"));
        reverified.ShouldNotBeNull();
        reverified!.ConversationId.ShouldNotBeNull(
            "Load-time backfill must rewrite the sidecar so a resolver-less store " +
            "still observes the stamp — otherwise every load round-trips through the " +
            "conversation store.");
        reverified.ConversationId!.Value.ShouldBe(legacy.ConversationId);
    }

    [Fact]
    public async Task File_SaveAsync_NoResolver_PreservesNullConversationId()
    {
        using var fixture = new FileFixture();
        var store = fixture.CreateStore(conversationStore: null);
        var session = await store.GetOrCreateAsync(SessionId.From("s-no-conv"), AgentId.From("a"));
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore(conversationStore: null).GetAsync(SessionId.From("s-no-conv"));
        reloaded!.ConversationId.ShouldBeNull();
    }

    // --- SqliteSessionStore (save-time + load-time + UPDATE row) ---

    [Fact]
    public async Task Sqlite_SaveAsync_NullConversationId_StampsLegacy_PersistsDurably()
    {
        using var fixture = new SqliteFixture();
        var conversations = new InMemoryConversationStore();
        var store = fixture.CreateStore(conversations);
        var agentId = AgentId.From("agent-sqlite-save");

        var session = await store.GetOrCreateAsync(SessionId.From("s-sqlite-orphan"), agentId);
        await store.SaveAsync(session);

        session.ConversationId.ShouldNotBeNull();
        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");

        // Brand-new store (no cache) — the stamp must be persisted at the row level.
        var verifyStore = fixture.CreateStore(conversationStore: null);
        var reloaded = await verifyStore.GetAsync(SessionId.From("s-sqlite-orphan"));
        reloaded!.ConversationId!.Value.ShouldBe(legacy.ConversationId);
    }

    [Fact]
    public async Task Sqlite_ListByConversationAsync_FindsBackfilledOrphan_AfterLoadTimeStamp()
    {
        // This is the contract that motivates DURABLE load-time backfill: the indexed
        // query SELECT … WHERE conversation_id = ? can only see orphan rows after the
        // row's conversation_id has been UPDATEd to point at the legacy conversation.
        using var fixture = new SqliteFixture();
        var conversations = new InMemoryConversationStore();
        var agentId = AgentId.From("agent-sqlite-listby");

        // 1) Save an orphan row via a no-resolver store (simulates pre-Phase-9 state).
        var orphanStore = fixture.CreateStore(conversationStore: null);
        var orphan = await orphanStore.GetOrCreateAsync(SessionId.From("s-orphan-row"), agentId);
        await orphanStore.SaveAsync(orphan);

        // 2) Reload through a Phase-9-aware store. The instance LoadSessionAsync path
        // must call BackfillLoadedSessionAsync which UPDATEs the row.
        var phase9Store = fixture.CreateStore(conversations);
        var reloaded = await phase9Store.GetAsync(SessionId.From("s-orphan-row"));
        reloaded.ShouldNotBeNull();
        reloaded!.ConversationId.ShouldNotBeNull();
        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");

        // 3) The indexed ListByConversationAsync query must now find the backfilled row
        // in a fresh store (no cache pollution).
        var queryStore = fixture.CreateStore(conversationStore: null);
        var matched = await queryStore.ListByConversationAsync(legacy.ConversationId);
        matched.ShouldContain(s => s.SessionId == SessionId.From("s-orphan-row"),
            "Load-time backfill must persist via UPDATE so ListByConversationAsync " +
            "discovers orphan sessions through the conversation_id index.");
    }

    [Fact]
    public async Task Sqlite_SaveAsync_WithExistingConversationId_DoesNotStamp()
    {
        using var fixture = new SqliteFixture();
        var conversations = new InMemoryConversationStore();
        var store = fixture.CreateStore(conversations);
        var agentId = AgentId.From("agent-sqlite-bound");
        var bound = ConversationId.Create();

        var session = await store.GetOrCreateAsync(SessionId.From("s-sqlite-bound"), agentId);
        session.ConversationId = bound;
        await store.SaveAsync(session);

        session.ConversationId!.Value.ShouldBe(bound);
        (await conversations.ListAsync(agentId))
            .Where(c => c.Title == $"legacy:{agentId.Value}")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Sqlite_SaveAsync_NoResolver_PreservesNullConversationId()
    {
        // Back-compat for test composition roots / older deployments that don't wire an
        // IConversationStore into the SQLite session store. Without a resolver, orphan
        // sessions stay orphan (no stamping). Plan-vs-impl SHOULD-CONSIDER #3.
        using var fixture = new SqliteFixture();
        var store = fixture.CreateStore(conversationStore: null);
        var session = await store.GetOrCreateAsync(SessionId.From("s-sq-no-resolver"), AgentId.From("a"));
        await store.SaveAsync(session);

        session.ConversationId.ShouldBeNull();

        // Reloading through another no-resolver store must still observe null.
        var reload = await fixture.CreateStore(conversationStore: null).GetAsync(SessionId.From("s-sq-no-resolver"));
        reload!.ConversationId.ShouldBeNull();
    }

    // --- Cross-store parity ---

    [Fact]
    public async Task AllStores_ProduceSameLegacyTitle_ForSameAgent()
    {
        // The canonical title "legacy:{agentId.Value}" is the cross-store invariant —
        // any store can write it and any other store can find it via Title lookup.
        var agentId = AgentId.From("agent-parity");
        var expectedTitle = $"legacy:{agentId.Value}";

        var sharedConversations = new InMemoryConversationStore();

        // InMemory
        var inMemStore = new InMemorySessionStore(redactor: null, conversationStore: sharedConversations, logger: null);
        var inMemSession = await inMemStore.GetOrCreateAsync(SessionId.From("s-im"), agentId);
        await inMemStore.SaveAsync(inMemSession);

        // File
        using var fileFixture = new FileFixture();
        var fileStore = fileFixture.CreateStore(sharedConversations);
        var fileSession = await fileStore.GetOrCreateAsync(SessionId.From("s-file"), agentId);
        await fileStore.SaveAsync(fileSession);

        // SQLite
        using var sqliteFixture = new SqliteFixture();
        var sqliteStore = sqliteFixture.CreateStore(sharedConversations);
        var sqliteSession = await sqliteStore.GetOrCreateAsync(SessionId.From("s-sq"), agentId);
        await sqliteStore.SaveAsync(sqliteSession);

        // All three sessions must point at the SAME single legacy conversation row,
        // proving the resolver is the single source of truth across store impls.
        var legacies = (await sharedConversations.ListAsync(agentId))
            .Where(c => c.Title == expectedTitle)
            .ToList();
        legacies.Count.ShouldBe(1, "All stores must converge on the same legacy conversation per agent.");
        var legacyId = legacies[0].ConversationId;
        inMemSession.ConversationId!.Value.ShouldBe(legacyId);
        fileSession.ConversationId!.Value.ShouldBe(legacyId);
        sqliteSession.ConversationId!.Value.ShouldBe(legacyId);
    }

    // --- ActiveSessionId binding (Hub reset regression for #615) ---

    [Fact]
    public async Task InMemory_SaveAsync_StampingActiveOrphan_BindsLegacyConversationActiveSessionId()
    {
        // Phase 9 / P9-B-1 (#615): legacy conversations are full-fledged conversations
        // that own their sessions, not just placeholders. When stamping an Active orphan
        // we MUST also bind the legacy conversation's ActiveSessionId so canonical reset
        // (DefaultConversationResetService.ResetActiveSessionAsync) can locate and seal
        // the session. Without this, GatewayHub.ResetSession's canonical-path branch
        // (taken when ConversationId is now non-null after backfill) silently no-ops
        // because the conversation has ActiveSessionId == null.
        var conversations = new InMemoryConversationStore();
        var store = new InMemorySessionStore(redactor: null, conversationStore: conversations, logger: null);
        var agentId = AgentId.From("agent-active-bind");
        var sessionId = SessionId.From("s-active-bind");

        var session = await store.GetOrCreateAsync(sessionId, agentId);
        session.Status.ShouldBe(SessionStatus.Active);
        await store.SaveAsync(session);

        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        legacy.ActiveSessionId.ShouldBe(sessionId,
            "Stamping an Active orphan must also bind the legacy conversation's " +
            "ActiveSessionId so the canonical reset path can locate the session.");
    }

    [Fact]
    public async Task InMemory_SaveAsync_StampingSealedOrphan_DoesNotBindActiveSessionId()
    {
        // Sealed sessions are history — they must not become the "active" pointer on
        // their legacy conversation. Only Active sessions are legitimate active-pointer
        // candidates.
        var conversations = new InMemoryConversationStore();
        var store = new InMemorySessionStore(redactor: null, conversationStore: conversations, logger: null);
        var agentId = AgentId.From("agent-sealed-orphan");
        var sessionId = SessionId.From("s-sealed-orphan");

        var session = await store.GetOrCreateAsync(sessionId, agentId);
        session.Status = SessionStatus.Sealed;
        await store.SaveAsync(session);

        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        legacy.ActiveSessionId.ShouldBeNull(
            "Sealed sessions are history — they must not be bound as the legacy " +
            "conversation's ActiveSessionId.");
    }

    [Theory]
    [InlineData(SessionStatus.Suspended)]
    [InlineData(SessionStatus.Expired)]
    public async Task InMemory_SaveAsync_StampingNonActiveOrphan_DoesNotBindActiveSessionId(SessionStatus nonActiveStatus)
    {
        // Mirrors the Sealed pin for the other two non-Active statuses (Suspended,
        // Expired). Pinned as a Theory so a future refactor that swaps the `== Active`
        // guard for a permissive predicate fails for every non-Active status.
        var conversations = new InMemoryConversationStore();
        var store = new InMemorySessionStore(redactor: null, conversationStore: conversations, logger: null);
        var agentId = AgentId.From($"agent-orphan-{nonActiveStatus.ToString().ToLowerInvariant()}");

        var session = await store.GetOrCreateAsync(SessionId.From($"s-{nonActiveStatus}"), agentId);
        session.Status = nonActiveStatus;
        await store.SaveAsync(session);

        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        legacy.ActiveSessionId.ShouldBeNull(
            $"{nonActiveStatus} sessions are not the active candidate — must not bind " +
            "the legacy conversation's ActiveSessionId.");
    }

    [Fact]
    public async Task InMemory_SaveAsync_StampingActiveOrphan_PreservesExistingActiveSessionPointer()
    {
        // Concurrency invariant: if another caller has already bound an ActiveSessionId
        // on the legacy conversation, stamping a different orphan MUST NOT clobber that
        // pointer. The new orphan still gets stamped with the conversation id, but the
        // active pointer keeps pointing at whoever bound it first. Stale pointers are
        // self-healed by DefaultConversationResetService when the pointed-at session is
        // missing from the store.
        var conversations = new InMemoryConversationStore();
        var store = new InMemorySessionStore(redactor: null, conversationStore: conversations, logger: null);
        var agentId = AgentId.From("agent-preserves-pointer");

        // First Active orphan binds the pointer.
        var firstId = SessionId.From("s-first-active");
        var firstSession = await store.GetOrCreateAsync(firstId, agentId);
        await store.SaveAsync(firstSession);

        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        legacy.ActiveSessionId.ShouldBe(firstId);

        // Second Active orphan must NOT clobber the pointer.
        var secondId = SessionId.From("s-second-active");
        var secondSession = await store.GetOrCreateAsync(secondId, agentId);
        await store.SaveAsync(secondSession);

        var refreshedLegacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        refreshedLegacy.ActiveSessionId.ShouldBe(firstId,
            "Stamping a second Active orphan must not clobber the existing " +
            "ActiveSessionId pointer — concurrency invariant.");
        secondSession.ConversationId!.Value.ShouldBe(legacy.ConversationId,
            "But the second orphan still gets stamped with the legacy conversation id.");
    }

    // --- Fixtures ---

    private sealed class FileFixture : IDisposable
    {
        public FileFixture()
        {
            FileSystem = new MockFileSystem();
            StorePath = Path.Combine(Path.GetTempPath(), "LegacyBackfillTests", Guid.NewGuid().ToString("N"));
            FileSystem.Directory.CreateDirectory(StorePath);
        }

        public string StorePath { get; }
        public MockFileSystem FileSystem { get; }

        public FileSessionStore CreateStore(IConversationStore? conversationStore)
            => new(StorePath, NullLogger<FileSessionStore>.Instance, FileSystem, conversationStore: conversationStore);

        public void Dispose()
        {
            if (FileSystem.Directory.Exists(StorePath))
                FileSystem.Directory.Delete(StorePath, true);
        }
    }

    private sealed class SqliteFixture : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public SqliteFixture()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"LegacyBackfillTests-{Guid.NewGuid():N}.db");
            _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString();
        }

        public SqliteSessionStore CreateStore(IConversationStore? conversationStore)
            => new(_connectionString, NullLogger<SqliteSessionStore>.Instance, conversationStore);

        public void Dispose()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(_dbPath))
                {
                    for (var i = 0; i < 5; i++)
                    {
                        try { File.Delete(_dbPath); return; }
                        catch (IOException) { if (i >= 4) break; Thread.Sleep(50); }
                    }
                }
            }
            catch (IOException) { /* cleanup best effort */ }
        }
    }
}
