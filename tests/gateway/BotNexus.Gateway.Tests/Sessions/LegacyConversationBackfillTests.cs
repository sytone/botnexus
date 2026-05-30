using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
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
        session.ConversationId.IsInitialized().ShouldBeFalse();
        await store.SaveAsync(session);

        session.ConversationId.IsInitialized().ShouldBeTrue();
        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        session.ConversationId.ShouldBe(legacy.ConversationId);
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

        session.ConversationId.ShouldBe(bound);
        var legacies = (await conversations.ListAsync(agentId))
            .Where(c => c.Title == $"legacy:{agentId.Value}")
            .ToList();
        legacies.ShouldBeEmpty(
            "Legacy resolver must not be invoked for an already-bound session — " +
            "stamping is a backfill, not a default-override.");
    }

    [Fact]
    public async Task InMemory_SaveAsync_NoResolver_PreservesUnsetConversationId()
    {
        // Back-compat for test composition roots that don't wire IConversationStore.
        // Without a resolver the InMemory store has nothing to backfill against, so the
        // ConversationId stays uninitialized. (SQLite fails loud in the same scenario —
        // see Sqlite_SaveAsync_NoResolver_ThrowsInvalidOperation.)
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s-no-resolver"), AgentId.From("a"));
        await store.SaveAsync(session);

        session.ConversationId.IsInitialized().ShouldBeFalse();
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

        var stampedIds = sessions.Select(s => s.ConversationId).Distinct().ToList();
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

        session.ConversationId.IsInitialized().ShouldBeTrue();

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
        prePhase9.ConversationId.IsInitialized().ShouldBeFalse();

        // Now load through a Phase-9-aware store; the load path must backfill.
        var loadStore = fixture.CreateStore(conversations);
        var loaded = await loadStore.GetAsync(SessionId.From("s-orphan-side"));
        loaded.ShouldNotBeNull();
        loaded!.ConversationId.IsInitialized().ShouldBeTrue();
        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        loaded.ConversationId.ShouldBe(legacy.ConversationId);

        // A third store instance (no cache; no resolver) must still see the durable stamp
        // — proves the load path rewrote the sidecar, not just the in-memory projection.
        var verifyStore = fixture.CreateStore(conversationStore: null);
        var reverified = await verifyStore.GetAsync(SessionId.From("s-orphan-side"));
        reverified.ShouldNotBeNull();
        reverified!.ConversationId.IsInitialized().ShouldBeTrue(
            "Load-time backfill must rewrite the sidecar so a resolver-less store " +
            "still observes the stamp — otherwise every load round-trips through the " +
            "conversation store.");
        reverified.ConversationId.ShouldBe(legacy.ConversationId);
    }

    [Fact]
    public async Task File_SaveAsync_NoResolver_PreservesUnsetConversationId()
    {
        // Back-compat for test composition roots / older deployments that don't wire an
        // IConversationStore into the File session store. Without a resolver, orphan
        // sessions stay unset. (SQLite fails loud in the same scenario — see
        // Sqlite_SaveAsync_NoResolver_ThrowsInvalidOperation.)
        using var fixture = new FileFixture();
        var store = fixture.CreateStore(conversationStore: null);
        var session = await store.GetOrCreateAsync(SessionId.From("s-no-conv"), AgentId.From("a"));
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore(conversationStore: null).GetAsync(SessionId.From("s-no-conv"));
        reloaded!.ConversationId.IsInitialized().ShouldBeFalse();
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

        session.ConversationId.IsInitialized().ShouldBeTrue();
        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");

        // Brand-new store (no cache) — the stamp must be persisted at the row level.
        var verifyStore = fixture.CreateStore(conversationStore: null);
        var reloaded = await verifyStore.GetAsync(SessionId.From("s-sqlite-orphan"));
        reloaded!.ConversationId.ShouldBe(legacy.ConversationId);
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

        // 1) Seed an orphan row directly (simulates pre-Phase-9 state on disk; bypasses
        // the post-P9-B-2 fail-loud writer that would refuse to persist an unset row).
        await fixture.SeedOrphanRowAsync(SessionId.From("s-orphan-row"), agentId);

        // 2) Reload through a Phase-9-aware store. The instance LoadSessionAsync path
        // must call BackfillLoadedSessionAsync which UPDATEs the row.
        var phase9Store = fixture.CreateStore(conversations);
        var reloaded = await phase9Store.GetAsync(SessionId.From("s-orphan-row"));
        reloaded.ShouldNotBeNull();
        reloaded!.ConversationId.IsInitialized().ShouldBeTrue();
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

        session.ConversationId.ShouldBe(bound);
        (await conversations.ListAsync(agentId))
            .Where(c => c.Title == $"legacy:{agentId.Value}")
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Sqlite_SaveAsync_NoResolver_ThrowsInvalidOperation()
    {
        // Phase 9 / P9-B-2 (#627): the SQLite writer is the durable backstop and must
        // fail loud when no resolver is configured and the session has no ConversationId.
        // Silently writing NULL would re-introduce the orphan condition the non-nullable
        // flip is supposed to prevent. (InMemory + File still tolerate this because they
        // are typically used in tests / lightweight composition roots where missing
        // ConversationIds are surfaced elsewhere.)
        using var fixture = new SqliteFixture();
        var store = fixture.CreateStore(conversationStore: null);
        var session = await store.GetOrCreateAsync(SessionId.From("s-sq-no-resolver"), AgentId.From("a"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => store.SaveAsync(session));
        ex.Message.ShouldContain("ConversationId");
        ex.Message.ShouldContain("s-sq-no-resolver");
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
        inMemSession.ConversationId.ShouldBe(legacyId);
        fileSession.ConversationId.ShouldBe(legacyId);
        sqliteSession.ConversationId.ShouldBe(legacyId);
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
        secondSession.ConversationId.ShouldBe(legacy.ConversationId,
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

        /// <summary>
        /// Seeds an orphan session row (conversation_id = NULL) by writing directly via
        /// raw SQL. Post-P9-B-2, the SqliteSessionStore writer fails loud on unset
        /// ConversationId — but the load-time backfill path must still handle pre-Phase-9
        /// rows that already exist on disk. Use this helper to construct the pre-existing
        /// orphan state that the backfill is designed to repair.
        /// </summary>
        public async Task SeedOrphanRowAsync(SessionId sessionId, AgentId agentId)
        {
            // Ensure schema exists by spinning up a normal store (ListAsync triggers
            // EnsureCreatedAsync). SqliteSessionStore holds no per-instance disposable
            // state — the schema lives on disk and connections come from the pool.
            var bootstrap = CreateStore(conversationStore: null);
            _ = await bootstrap.ListAsync();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (id, agent_id, channel_type, caller_id, session_type, participants_json, status, metadata, created_at, updated_at, conversation_id)
                VALUES ($id, $agentId, NULL, NULL, $sessionType, '[]', $status, '{}', $createdAt, $updatedAt, NULL)
                """;
            cmd.Parameters.AddWithValue("$id", sessionId.Value);
            cmd.Parameters.AddWithValue("$agentId", agentId.Value);
            cmd.Parameters.AddWithValue("$sessionType", SessionType.UserAgent.Value);
            cmd.Parameters.AddWithValue("$status", SessionStatus.Active.ToString());
            cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

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

    // --- FileSessionStore eager startup sweep (P9-B-2 / #627) ---
    //
    // The sweep runs once per process (via SemaphoreSlim + bool flag) on the first
    // GetAsync / GetOrCreateAsync / SaveAsync / EnumerateSessionsAsync call. It mirrors
    // SqliteSessionStore.MigrateOrphanedSessionsAsync: pre-Phase-9 orphan sidecars on
    // disk get stamped with the agent's legacy:{agentId} conversation, and the most-
    // recently-updated Active orphan per agent is bound as the legacy conversation's
    // ActiveSessionId.

    [Fact]
    public async Task File_EagerSweep_BindsMostRecentlyUpdatedActiveOrphan_AsConversationActiveSession()
    {
        // Seed three orphan sidecars for the same agent at distinct UpdatedAt timestamps
        // (no conversation store wired, so no stamping happens). Then open a fresh store
        // WITH a resolver; the first GetAsync triggers EnsureMigratedAsync which must
        // pick the most-recently-updated Active orphan as the bound ActiveSessionId.
        using var fixture = new FileFixture();
        var agentId = AgentId.From("agent-eager-bind");

        var preStore = fixture.CreateStore(conversationStore: null);
        var oldest = await preStore.GetOrCreateAsync(SessionId.From("s-old"), agentId);
        oldest.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        await preStore.SaveAsync(oldest);

        var middle = await preStore.GetOrCreateAsync(SessionId.From("s-mid"), agentId);
        middle.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        await preStore.SaveAsync(middle);

        var newest = await preStore.GetOrCreateAsync(SessionId.From("s-new"), agentId);
        newest.UpdatedAt = DateTimeOffset.UtcNow;
        await preStore.SaveAsync(newest);

        // Sanity: all three sidecars are orphans on disk before sweep.
        oldest.ConversationId.IsInitialized().ShouldBeFalse();
        middle.ConversationId.IsInitialized().ShouldBeFalse();
        newest.ConversationId.IsInitialized().ShouldBeFalse();

        var conversations = new InMemoryConversationStore();
        var sweepStore = fixture.CreateStore(conversations);

        // Trigger sweep — any public method works; pick GetAsync on an arbitrary sidecar.
        _ = await sweepStore.GetAsync(SessionId.From("s-old"));

        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        legacy.ActiveSessionId.ShouldBe(
            SessionId.From("s-new"),
            "Eager sweep must bind the most-recently-updated Active orphan as the legacy " +
            "conversation's ActiveSessionId — mirrors SqliteSessionStore semantics.");

        // All three sidecars must also be stamped (regardless of which was bound).
        var verifyStore = fixture.CreateStore(conversationStore: null);
        var reloadedOld = await verifyStore.GetAsync(SessionId.From("s-old"));
        var reloadedMid = await verifyStore.GetAsync(SessionId.From("s-mid"));
        var reloadedNew = await verifyStore.GetAsync(SessionId.From("s-new"));
        reloadedOld!.ConversationId.ShouldBe(legacy.ConversationId);
        reloadedMid!.ConversationId.ShouldBe(legacy.ConversationId);
        reloadedNew!.ConversationId.ShouldBe(legacy.ConversationId);
    }

    [Fact]
    public async Task File_EagerSweep_DoesNotBindSealedOrSuspendedOrphans_AsActive()
    {
        // The most-recently-updated orphan is Sealed; an older orphan is still Active.
        // The bind step must skip the Sealed orphan and pin the Active one as
        // ActiveSessionId — Sealed/Suspended/Expired sessions are history, not live work.
        // Stamping (the conversation_id field) still applies to all orphans regardless
        // of status — that's separate from the ActiveSessionId pointer.
        using var fixture = new FileFixture();
        var agentId = AgentId.From("agent-eager-status");

        var preStore = fixture.CreateStore(conversationStore: null);

        var olderActive = await preStore.GetOrCreateAsync(SessionId.From("s-active-old"), agentId);
        olderActive.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        // Status defaults to Active.
        await preStore.SaveAsync(olderActive);

        var newerSealed = await preStore.GetOrCreateAsync(SessionId.From("s-sealed-new"), agentId);
        newerSealed.UpdatedAt = DateTimeOffset.UtcNow;
        newerSealed.Status = SessionStatus.Sealed;
        await preStore.SaveAsync(newerSealed);

        var conversations = new InMemoryConversationStore();
        var sweepStore = fixture.CreateStore(conversations);
        _ = await sweepStore.GetAsync(SessionId.From("s-active-old"));

        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        legacy.ActiveSessionId.ShouldBe(
            SessionId.From("s-active-old"),
            "Sealed orphans must NOT be bound as ActiveSessionId even when more recently " +
            "updated than Active siblings — only Active sessions are live work.");

        // Stamping still applies to the Sealed sidecar — it's part of the conversation
        // history; the bind decision is independent of the stamp decision.
        var verifyStore = fixture.CreateStore(conversationStore: null);
        var reloadedSealed = await verifyStore.GetAsync(SessionId.From("s-sealed-new"));
        reloadedSealed!.ConversationId.ShouldBe(
            legacy.ConversationId,
            "Sealed orphans still get their conversation_id stamped — the sweep treats " +
            "stamping (membership) and binding (active pointer) as independent decisions.");
    }

    [Fact]
    public async Task File_EagerSweep_RunsOnceAcrossConcurrentCallers_NoDuplicateMigration()
    {
        // Fire N concurrent first-touch calls on a fresh store and prove the sweep
        // executes exactly once. Probe via the internal MigrationInvocationCount
        // counter that increments inside the per-store EnsureMigratedAsync gate;
        // this is decoupled from any inner resolver ListAsync call counts (the
        // resolver legitimately issues 1-3 ListAsync calls per sweep depending on
        // cache state, so counting at the conversation store conflates correctness
        // with resolver implementation detail).
        using var fixture = new FileFixture();
        var agentId = AgentId.From("agent-eager-concurrent");

        // Seed one orphan so the sweep has work to do (otherwise it would short-circuit
        // before invoking the resolver and the migration would be vacuous).
        var preStore = fixture.CreateStore(conversationStore: null);
        var orphan = await preStore.GetOrCreateAsync(SessionId.From("s-concurrent-orphan"), agentId);
        await preStore.SaveAsync(orphan);

        var conversations = new InMemoryConversationStore();
        var sweepStore = fixture.CreateStore(conversations);

        // 20 concurrent first-touch calls on the SAME store instance — only one should
        // execute the sweep; the other 19 must block on _migrationLock and then short
        // out via _migrated == true.
        const int concurrency = 20;
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ =>
            sweepStore.GetAsync(SessionId.From("s-concurrent-orphan"))));

        sweepStore.MigrationInvocationCount.ShouldBe(
            1,
            "Eager sweep must execute exactly once across concurrent first-touch callers. " +
            $"Observed {sweepStore.MigrationInvocationCount} MigrateOrphanedSessionsAsync " +
            "invocations (expected 1). If >1, the SemaphoreSlim + _migrated double-check " +
            "gate has regressed and the sweep is racing itself.");

        // Sanity: the orphan was actually migrated (stamped + bound). If the migration
        // ran but did nothing, the count assertion alone would be vacuous.
        var legacy = (await conversations.ListAsync(agentId))
            .Single(c => c.Title == $"legacy:{agentId.Value}");
        legacy.ActiveSessionId.ShouldBe(SessionId.From("s-concurrent-orphan"));
    }

    [Fact]
    public async Task File_EagerSweep_CancellationDoesNotPoisonSubsequentCalls()
    {
        // First caller cancels mid-sweep (we use a delayable conversation store to wedge
        // the resolver inside the lock). The cancelled attempt MUST NOT set _migrated —
        // a fresh call with its own token must retry and succeed.
        using var fixture = new FileFixture();
        var agentId = AgentId.From("agent-eager-cancel");

        // Seed one orphan so the sweep has work to do.
        var preStore = fixture.CreateStore(conversationStore: null);
        var orphan = await preStore.GetOrCreateAsync(SessionId.From("s-cancel-orphan"), agentId);
        await preStore.SaveAsync(orphan);

        var gate = new DelayableConversationStore(new InMemoryConversationStore());
        var sweepStore = fixture.CreateStore(gate);

        // First call: cancellation token will be cancelled while the resolver is wedged
        // inside ListAsync.
        using var cts = new CancellationTokenSource();
        var firstCallStarted = gate.NextListAsyncStarted();
        var firstCall = Task.Run(() => sweepStore.GetAsync(SessionId.From("s-cancel-orphan"), cts.Token));

        // Wait until the resolver has entered ListAsync, then cancel.
        await firstCallStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        gate.ReleaseAll();

        var firstException = await Should.ThrowAsync<OperationCanceledException>(() => firstCall);
        firstException.ShouldNotBeNull();

        // Second call with a fresh token must NOT find a stale _migrated == true and skip;
        // it must retry the sweep and succeed. Reset the gate so the second call runs
        // straight through without wedging.
        gate.ResetGate();
        gate.ReleaseAll();
        var second = await sweepStore.GetAsync(SessionId.From("s-cancel-orphan"));

        second.ShouldNotBeNull();
        second!.ConversationId.IsInitialized().ShouldBeTrue(
            "Cancellation of the first sweep attempt must not leave _migrated = true. " +
            "Subsequent callers must retry the sweep with their own token and see the " +
            "orphan stamped — otherwise a single cancellation poisons the store for the " +
            "rest of the process lifetime.");

        // Underlying store also has the legacy conversation row created.
        (await gate.Inner.ListAsync(agentId))
            .Count(c => c.Title == $"legacy:{agentId.Value}")
            .ShouldBe(1);
    }

    // --- Helpers for the eager-sweep tests ---

    /// <summary>
    /// Wraps an <see cref="IConversationStore"/> with a manually-controlled gate on
    /// <see cref="ListAsync"/>. Used by the cancellation test to wedge the resolver
    /// inside its fast-path call so we can deterministically cancel mid-sweep.
    /// </summary>
    private sealed class DelayableConversationStore : IConversationStore
    {
        public IConversationStore Inner { get; }
        private TaskCompletionSource<bool> _listAsyncStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DelayableConversationStore(IConversationStore inner) => Inner = inner;

        public Task NextListAsyncStarted() => _listAsyncStarted.Task;
        public void ReleaseAll() => _releaseGate.TrySetResult(true);
        public void ResetGate()
        {
            _releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _listAsyncStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
        {
            _listAsyncStarted.TrySetResult(true);
            await Task.WhenAny(_releaseGate.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return await Inner.ListAsync(agentId, ct).ConfigureAwait(false);
        }

        public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default) => Inner.GetAsync(conversationId, ct);
        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default) => Inner.ListForCitizenAsync(citizen, ct);
        public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default) => Inner.CreateAsync(conversation, ct);
        public Task SaveAsync(Conversation conversation, CancellationToken ct = default) => Inner.SaveAsync(conversation, ct);
        public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default)
            => Inner.AddParticipantsAsync(conversationId, participants, ct);
        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default) => Inner.ArchiveAsync(conversationId, ct);
        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
            => Inner.ResolveByBindingAsync(agentId, channelType, channelAddress, ct);
        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(AgentId? agentId = null, CancellationToken ct = default)
            => Inner.GetSummariesAsync(agentId, ct);
    }
}
