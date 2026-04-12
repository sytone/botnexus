using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
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

        reloaded.Should().NotBeNull();
        reloaded!.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task GetOrCreateAsync_WithExistingSession_ReturnsExistingSession()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var created = await store.GetOrCreateAsync("s1", "agent-a");

        var loaded = await store.GetOrCreateAsync("s1", "agent-b");

        loaded.Should().BeSameAs(created);
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

        reloaded!.History.Should().ContainSingle(e => e.Content == "hello");
    }

    [Fact]
    public async Task GetAsync_WithUnknownSession_ReturnsNull()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var session = await store.GetAsync("missing");

        session.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithExistingSession_RemovesHistoryAndMetadataFiles()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var sessionId = "session/with special";
        var session = await store.GetOrCreateAsync(sessionId, "agent-a");
        await store.SaveAsync(session);
        var encodedName = Uri.EscapeDataString(sessionId);

        await store.DeleteAsync(sessionId);

        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.jsonl")).Should().BeFalse();
        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.meta.json")).Should().BeFalse();
    }

    [Fact]
    public async Task ArchiveAsync_RenamesFiles()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        const string sessionId = "archive-me";
        var encodedName = Uri.EscapeDataString(sessionId);
        var session = await store.GetOrCreateAsync(sessionId, "agent-a");
        session.History.Add(new SessionEntry { Role = MessageRole.User, Content = "persist-me" });
        await store.SaveAsync(session);

        await store.ArchiveAsync(sessionId);

        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.jsonl")).Should().BeFalse();
        fixture.FileSystem.File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.meta.json")).Should().BeFalse();
        fixture.FileSystem.Directory.GetFiles(fixture.StorePath, $"{encodedName}.jsonl.archived.*").Should().ContainSingle();
        fixture.FileSystem.Directory.GetFiles(fixture.StorePath, $"{encodedName}.meta.json.archived.*").Should().ContainSingle();
        (await store.GetAsync(sessionId)).Should().BeNull();
    }

    [Fact]
    public async Task ArchiveAsync_WhenNoFilesExist_DoesNotThrow()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var act = () => store.ArchiveAsync("missing");

        await act.Should().NotThrowAsync();
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

        newSession.Should().NotBeSameAs(oldSession);
        newSession.History.Should().BeEmpty();
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

        allSessions.Should().HaveCount(3);
        filtered.Should().OnlyContain(s => s.AgentId == "agent-a");
    }

    [Fact]
    public async Task ListByChannelAsync_FiltersByAgentAndNormalizedChannel_OrderedByCreatedAtDesc()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        await store.SaveAsync(new GatewaySession
        {
            SessionId = "s-old",
            AgentId = "agent-a",
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = "s-new",
            AgentId = "agent-a",
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = "s-other-channel",
            AgentId = "agent-a",
            ChannelType = ChannelKey.From("telegram")
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = "s-null-channel",
            AgentId = "agent-a"
        });

        var sessions = await store.ListByChannelAsync("agent-a", ChannelKey.From("web chat"));

        sessions.Select(s => s.SessionId).Should().Equal("s-new", "s-old");
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

        allSessions.Should().HaveCount(25);
        allSessions.Should().OnlyContain(s => s.History.Count == 1);
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

        reloaded!.History.Should().HaveCount(1000);
        reloaded.History[999].Content.Should().Be("line-999");
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

        reloaded.Should().NotBeNull();
        reloaded!.SessionId.Should().Be(sessionId);
        reloaded.History.Should().ContainSingle(e => e.Content == "hello");
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

        reloaded.Should().NotBeNull();
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
                "C:\\",
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




