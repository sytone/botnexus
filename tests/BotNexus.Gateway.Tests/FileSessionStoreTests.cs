using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

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
        session.History.Add(new SessionEntry { Role = "user", Content = "hello" });

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

        File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.jsonl")).Should().BeFalse();
        File.Exists(Path.Combine(fixture.StorePath, $"{encodedName}.meta.json")).Should().BeFalse();
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
    public async Task ConcurrentAccess_WithMultipleSessions_DoesNotCorruptData()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var tasks = Enumerable.Range(0, 25)
            .Select(async i =>
            {
                var session = await store.GetOrCreateAsync($"s{i}", "agent-a");
                session.History.Add(new SessionEntry { Role = "user", Content = $"msg-{i}" });
                await store.SaveAsync(session);
            });

        await Task.WhenAll(tasks);
        var allSessions = await fixture.CreateStore().ListAsync();

        allSessions.Should().HaveCount(25);
        allSessions.Should().OnlyContain(s => s.History.Count == 1);
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
            StorePath = Path.Combine(
                AppContext.BaseDirectory,
                "FileSessionStoreTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(StorePath);
        }

        public string StorePath { get; }

        public FileSessionStore CreateStore()
            => new(StorePath, NullLogger<FileSessionStore>.Instance);

        public void Dispose()
        {
            if (Directory.Exists(StorePath))
                Directory.Delete(StorePath, true);
        }
    }
}
