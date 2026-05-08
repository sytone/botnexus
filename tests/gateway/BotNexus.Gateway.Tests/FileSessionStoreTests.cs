using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class FileSessionStoreTests
{
    [Fact]
    public async Task GetOrCreateAsync_WithUnknownSession_CreatesAndPersistsSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var session = await store.GetOrCreateAsync("s1", "agent-a");
        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync("s1");

        reloaded.ShouldNotBeNull();
        reloaded!.SessionId.Value.ShouldBe("s1");
    }

    [Fact]
    public async Task GetOrCreateAsync_WithExistingSession_ReturnsExistingSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var created = await store.GetOrCreateAsync("s1", "agent-a");

        var loaded = await store.GetOrCreateAsync("s1", "agent-b");

        loaded.ShouldBeSameAs(created);
    }

    [Fact]
    public async Task SaveAsync_WithHistory_PersistsHistoryEntries()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync("s1");

        reloaded!.History.Where(e => e.Content == "hello").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetAsync_WithUnknownSession_ReturnsNull()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var session = await store.GetAsync("missing");

        session.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithExistingSession_RemovesHistoryAndMetadataFiles()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var sessionId = "session/with special";
        var session = await store.GetOrCreateAsync(sessionId, "agent-a");
        await store.SaveAsync(session);
        var encodedName = SessionFileNames.SanitizeSessionId(sessionId);

        await store.DeleteAsync(sessionId);

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
        var session = await store.GetOrCreateAsync(sessionId, "agent-a");
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "persist-me" });
        await store.SaveAsync(session);

        await store.ArchiveAsync(sessionId);

        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.jsonl")).ShouldBeFalse();
        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.meta.json")).ShouldBeFalse();
        fixture.FileSystem.Directory.GetFiles(fixture.StorePath, $"{encodedName}.jsonl.archived.*").ShouldHaveSingleItem();
        fixture.FileSystem.Directory.GetFiles(fixture.StorePath, $"{encodedName}.meta.json.archived.*").ShouldHaveSingleItem();
        (await store.GetAsync(sessionId)).ShouldBeNull();
    }

    [Fact]
    public async Task ArchiveAsync_WhenNoFilesExist_DoesNotThrow()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        Func<Task> act = () => store.ArchiveAsync("missing");

        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task ArchiveAsync_ThenGetOrCreate_ReturnsNewSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var oldSession = await store.GetOrCreateAsync("s1", "agent-a");
        oldSession.History.Add(new SessionEntry { Role = MessageRole.User, Content = "old-message" });
        await store.SaveAsync(oldSession);

        await store.ArchiveAsync("s1");
        var newSession = await store.GetOrCreateAsync("s1", "agent-a");

        newSession.ShouldNotBeSameAs(oldSession);
        newSession.History.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_WithAndWithoutFilter_ReturnsExpectedSessions()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await CreateAndSaveAsync(store, "s1", "agent-a");
        await CreateAndSaveAsync(store, "s2", "agent-b");
        await CreateAndSaveAsync(store, "s3", "agent-a");

        var allSessions = await store.ListAsync();
        var filtered = await store.ListAsync("agent-a");

        allSessions.Count().ShouldBe(3);
        filtered.ShouldAllBe(s => s.AgentId == "agent-a");
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

        var sessions = await store.ListByChannelAsync("agent-a", ChannelKey.From("web chat"));

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
                var session = await store.GetOrCreateAsync($"s{i}", "agent-a");
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
        var session = await store.GetOrCreateAsync("large", "agent-a");
        for (var i = 0; i < 1000; i++)
            session.History.Add(new SessionEntry { Role = MessageRole.User, Content = $"line-{i}" });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync("large");

        reloaded!.History.Count().ShouldBe(1000);
        reloaded.History[999].Content.ShouldBe("line-999");
    }

    [Fact]
    public async Task GetOrCreateAsync_WithSpecialCharactersInSessionId_RoundTrips()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        const string sessionId = "s/日本語?:*&%20";
        var created = await store.GetOrCreateAsync(sessionId, "agent-a");
        created.History.Add(new SessionEntry { Role = MessageRole.User, Content = "hello" });

        await store.SaveAsync(created);
        var reloaded = await fixture.CreateStore().GetAsync(sessionId);

        reloaded.ShouldNotBeNull();
        reloaded!.SessionId.Value.ShouldBe(sessionId);
        reloaded.History.Where(e => e.Content == "hello").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ConcurrentReadWrite_SameSession_DoesNotThrowAndRemainsReadable()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await store.GetOrCreateAsync("shared", "agent-a");

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                var session = await store.GetOrCreateAsync("shared", "agent-a");
                session.UpdatedAt = DateTimeOffset.UtcNow;
                await store.SaveAsync(session);
            }
        });
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
                _ = await store.GetAsync("shared");
        });

        await Task.WhenAll(writer, reader);
        var reloaded = await fixture.CreateStore().GetAsync("shared");

        reloaded.ShouldNotBeNull();
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
                new BotNexus.Domain.Primitives.SessionParticipant { Type = BotNexus.Domain.Primitives.ParticipantType.Agent, Id = "agent-a" }
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

    private static async Task CreateAndSaveAsync(FileSessionStore store, string sessionId, string agentId)
    {
        var session = await store.GetOrCreateAsync(sessionId, agentId);
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



