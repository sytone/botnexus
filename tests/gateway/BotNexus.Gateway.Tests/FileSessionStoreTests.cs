using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class FileSessionStoreTests
{
    [Fact]
    public async Task SaveAsync_PreservesConversationId_AcrossReload()
    {
        // Regression pin (F-7): FileSessionStore historically dropped Session.ConversationId
        // on round-trip because the SessionMeta sidecar record didn't include the field.
        // After a server restart with FileSessionStore, every session became orphaned
        // (ConversationId == null), severing the conversation -> sessions linkage.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var conversationId = ConversationId.Create();

        var session = await store.GetOrCreateAsync(SessionId.From("s-with-conv"), AgentId.From("agent-a"));
        session.Session.ConversationId = conversationId;
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s-with-conv"));

        reloaded.ShouldNotBeNull();
        reloaded.Session.ConversationId.ShouldNotBeNull(
            "FileSessionStore lost ConversationId on round-trip — sessions go orphan on restart (F-7)");
        reloaded.Session.ConversationId!.Value.ShouldBe(
            conversationId,
            "FileSessionStore corrupted ConversationId on round-trip");
    }

    [Fact]
    public async Task SaveAsync_PreservesNullConversationId_AcrossReload()
    {
        // Companion to the above — a session saved with null ConversationId must
        // round-trip as null (not as some sentinel string, not as an exception).
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var session = await store.GetOrCreateAsync(SessionId.From("s-no-conv"), AgentId.From("agent-a"));
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s-no-conv"));

        reloaded.ShouldNotBeNull();
        reloaded.Session.ConversationId.ShouldBeNull();
    }

    [Fact]
    public async Task GetOrCreateAsync_WithUnknownSession_CreatesAndPersistsSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s1"));

        reloaded.ShouldNotBeNull();
        reloaded!.SessionId.Value.ShouldBe("s1");
    }

    [Fact]
    public async Task GetOrCreateAsync_WithExistingSession_ReturnsExistingSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var created = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        var loaded = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-b"));

        loaded.ShouldBeSameAs(created);
    }

    [Fact]
    public async Task SaveAsync_WithHistory_PersistsHistoryEntries()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("s1"));

        reloaded!.History.Where(e => e.Content == "hello").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetAsync_WithUnknownSession_ReturnsNull()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var session = await store.GetAsync(SessionId.From("missing"));

        session.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithExistingSession_RemovesHistoryAndMetadataFiles()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var sessionId = "session/with special";
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From("agent-a"));
        await store.SaveAsync(session);
        var encodedName = SessionFileNames.SanitizeSessionId(sessionId);

        await store.DeleteAsync(SessionId.From(sessionId));

        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.jsonl")).ShouldBeFalse();
        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.meta.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task ArchiveAsync_RenamesFiles()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        const string sessionId = "archive-me";
        var encodedName = SessionFileNames.SanitizeSessionId(sessionId);
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From("agent-a"));
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "persist-me" });
        await store.SaveAsync(session);

        await store.ArchiveAsync(SessionId.From(sessionId));

        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.jsonl")).ShouldBeFalse();
        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.meta.json")).ShouldBeFalse();
        fixture.FileSystem.Directory.GetFiles(fixture.StorePath, $"{encodedName}.jsonl.archived.*").ShouldHaveSingleItem();
        fixture.FileSystem.Directory.GetFiles(fixture.StorePath, $"{encodedName}.meta.json.archived.*").ShouldHaveSingleItem();
        (await store.GetAsync(SessionId.From(sessionId))).ShouldBeNull();
    }

    [Fact]
    public async Task ArchiveAsync_WhenNoFilesExist_DoesNotThrow()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        Func<Task> act = () => store.ArchiveAsync(SessionId.From("missing"));

        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task ArchiveAsync_ThenGetOrCreate_ReturnsNewSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var oldSession = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        oldSession.History.Add(new SessionEntry { Role = MessageRole.User, Content = "old-message" });
        await store.SaveAsync(oldSession);

        await store.ArchiveAsync(SessionId.From("s1"));
        var newSession = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        newSession.ShouldNotBeSameAs(oldSession);
        newSession.History.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_WithAndWithoutFilter_ReturnsExpectedSessions()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await CreateAndSaveAsync(store, "s1", AgentId.From("agent-a"));
        await CreateAndSaveAsync(store, "s2", AgentId.From("agent-b"));
        await CreateAndSaveAsync(store, "s3", AgentId.From("agent-a"));

        var allSessions = await store.ListAsync();
        var filtered = await store.ListAsync(AgentId.From("agent-a"));

        allSessions.Count().ShouldBe(3);
        filtered.ShouldAllBe(s => s.AgentId.Value == "agent-a");
    }

    [Fact]
    public async Task ListByChannelAsync_FiltersByAgentAndNormalizedChannel_OrderedByCreatedAtDesc()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-old"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-new"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-other-channel"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("telegram")
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("s-null-channel"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        });

        var sessions = await store.ListByChannelAsync(AgentId.From("agent-a"), ChannelKey.From("web chat"));

        sessions.Select(s => s.SessionId.Value).ShouldBe(new[] { "s-new", "s-old" }, ignoreOrder: false);
    }

    [Fact]
    public async Task ConcurrentAccess_WithMultipleSessions_DoesNotCorruptData()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var tasks = Enumerable.Range(0, 25)
            .Select(async i =>
            {
                var session = await store.GetOrCreateAsync(SessionId.From($"s{i}"), AgentId.From("agent-a"));
                session.History.Add(new SessionEntry { Role = MessageRole.User, Content = $"msg-{i}" });
                await store.SaveAsync(session);
            });

        await Task.WhenAll(tasks);
        var allSessions = await fixture.CreateStore().ListAsync();

        allSessions.Count().ShouldBe(25);
        allSessions.ShouldAllBe(s => s.History.Count == 1);
    }

    [Fact]
    public async Task SaveAsync_WithLargeHistory_PersistsAllEntries()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("large"), AgentId.From("agent-a"));
        for (var i = 0; i < 1000; i++)
            session.History.Add(new SessionEntry { Role = MessageRole.User, Content = $"line-{i}" });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("large"));

        reloaded!.History.Count().ShouldBe(1000);
        reloaded.History[999].Content.ShouldBe("line-999");
    }

    [Fact]
    public async Task GetOrCreateAsync_WithSpecialCharactersInSessionId_RoundTrips()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        const string sessionId = "s/日本語?:*&%20";
        var created = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From("agent-a"));
        created.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });

        await store.SaveAsync(created);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From(sessionId));

        reloaded.ShouldNotBeNull();
        reloaded!.SessionId.Value.ShouldBe(sessionId);
        reloaded.History.Where(e => e.Content == "hello").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ConcurrentReadWrite_SameSession_DoesNotThrowAndRemainsReadable()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await store.GetOrCreateAsync(SessionId.From("shared"), AgentId.From("agent-a"));

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                var session = await store.GetOrCreateAsync(SessionId.From("shared"), AgentId.From("agent-a"));
                session.UpdatedAt = DateTimeOffset.UtcNow;
                await store.SaveAsync(session);
            }
        });
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
                _ = await store.GetAsync(SessionId.From("shared"));
        });

        await Task.WhenAll(writer, reader);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("shared"));

        reloaded.ShouldNotBeNull();
    }

    // --- ListByConversationAsync: F-7 contract pins (FileSessionStore) ---
    //
    // These exercise the SAME 5 invariants as InMemorySessionStoreTests but go through
    // the on-disk round-trip (which is exactly where F-7 originated). Two-store-reload
    // pattern proves the invariants survive a process restart.

    private static async Task SeedConversationFixtureAsync(FileSessionStore store, DateTimeOffset baseTime)
    {
        var convA = ConversationId.From("conv-a");
        var convB = ConversationId.From("conv-b");

        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a-active"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime.AddMinutes(10),
            Status = SessionStatus.Active,
            Session = { ConversationId = convA }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a-sealed"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime,
            Status = SessionStatus.Sealed,
            Session = { ConversationId = convA }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-a-other-agent"),
            AgentId = AgentId.From("agent-y"),
            CreatedAt = baseTime.AddMinutes(5),
            Status = SessionStatus.Active,
            Session = { ConversationId = convA }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-b"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime.AddMinutes(20),
            Status = SessionStatus.Active,
            Session = { ConversationId = convB }
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("s-orphan"),
            AgentId = AgentId.From("agent-x"),
            CreatedAt = baseTime.AddMinutes(15),
            Status = SessionStatus.Active
        });
    }

    [Fact]
    public async Task ListByConversationAsync_AcrossReload_ReturnsActiveAndSealedSessions_InCreatedAtAscOrder()
    {
        // Invariants 1+3 combined, on disk: includes Active+Sealed AND chronological order
        // survives a full second-store reload (the F-7 originating scenario).
        using var fixture = new StoreFixture();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await SeedConversationFixtureAsync(fixture.CreateStore(), baseTime);

        var reloadedStore = fixture.CreateStore();
        var sessions = await reloadedStore.ListByConversationAsync(ConversationId.From("conv-a"));

        sessions.Select(s => s.SessionId.Value)
            .ShouldBe(new[] { "s-a-sealed", "s-a-other-agent", "s-a-active" }, ignoreOrder: false,
                customMessage: "FileSessionStore ListByConversationAsync did not return chronological Active+Sealed slice after reload");
    }

    [Fact]
    public async Task ListByConversationAsync_AcrossReload_ExcludesOtherConversations_AndOrphans()
    {
        using var fixture = new StoreFixture();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await SeedConversationFixtureAsync(fixture.CreateStore(), baseTime);

        var sessions = await fixture.CreateStore()
            .ListByConversationAsync(ConversationId.From("conv-a"));

        sessions.Select(s => s.SessionId.Value).ShouldNotContain("s-b");
        sessions.Select(s => s.SessionId.Value).ShouldNotContain("s-orphan");
    }

    [Fact]
    public async Task ListByConversationAsync_ReturnsEmptyList_ForUnknownConversation()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var sessions = await store.ListByConversationAsync(ConversationId.From("nope"));

        sessions.ShouldNotBeNull();
        sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListByConversationAsync_AcrossReload_WithAgentFilter_NarrowsToOwner()
    {
        using var fixture = new StoreFixture();
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await SeedConversationFixtureAsync(fixture.CreateStore(), baseTime);

        var sessions = await fixture.CreateStore()
            .ListByConversationAsync(ConversationId.From("conv-a"), agentId: AgentId.From("agent-x"));

        sessions.Select(s => s.SessionId.Value)
            .ShouldBe(new[] { "s-a-sealed", "s-a-active" }, ignoreOrder: false);
    }

    [Fact]
    public async Task GetExistenceAsync_ReturnsOwnedAndParticipantSessions_WithFiltersApplied()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("owned"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            SessionType = BotNexus.Domain.Primitives.SessionType.UserAgent,
            CreatedAt = now.AddDays(-2)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("participant"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-b"),
            SessionType = BotNexus.Domain.Primitives.SessionType.Cron,
            Participants =
            [
                new BotNexus.Domain.Primitives.SessionParticipant { CitizenId = CitizenId.Of(BotNexus.Domain.Primitives.AgentId.From("agent-a")) }
            ],
            CreatedAt = now.AddDays(-1)
        });

        var sessions = await store.GetExistenceAsync(
            BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            new ExistenceQuery
            {
                TypeFilter = BotNexus.Domain.Primitives.SessionType.Cron,
                From = now.AddDays(-1.5),
                Limit = 10
            });

        sessions.Select(session => session.SessionId.Value).ShouldHaveSingleItem().ShouldBe("participant");
    }

    private static async Task CreateAndSaveAsync(FileSessionStore store, string sessionId, AgentId agentId)
    {
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), agentId);
        await store.SaveAsync(session);
    }

    private sealed class StoreFixture : IDisposable
    {
        public StoreFixture()
        {
            FileSystem = new MockFileSystem();
            StorePath = Path.Combine(
                Path.GetTempPath(),
                "FileSessionStoreTests",
                Guid.NewGuid().ToString("N"));
            FileSystem.Directory.CreateDirectory(StorePath);
        }

        public string StorePath { get; }
        public MockFileSystem FileSystem { get; }

        public FileSessionStore CreateStore()
            => new(StorePath, NullLogger<FileSessionStore>.Instance, FileSystem);

        public void Dispose()
        {
            if (FileSystem.Directory.Exists(StorePath))
                FileSystem.Directory.Delete(StorePath, true);
        }
    }
}



