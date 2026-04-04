using BotNexus.Core.Models;
using BotNexus.Session;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class SessionTests : IDisposable
{
    private readonly string _tempPath;
    private readonly SessionManager _sessionManager;

    public SessionTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"botnexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempPath);
        _sessionManager = new SessionManager(_tempPath, NullLogger<SessionManager>.Instance);
    }

    [Fact]
    public async Task GetOrCreate_NewSession_ReturnsEmptySession()
    {
        var session = await _sessionManager.GetOrCreateAsync("test:session1", "agent1");

        session.Should().NotBeNull();
        session.Key.Should().Be("test:session1");
        session.AgentName.Should().Be("agent1");
        session.History.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOrCreate_ExistingSession_ReturnsSameSession()
    {
        var session1 = await _sessionManager.GetOrCreateAsync("test:session2", "agent1");
        session1.AddEntry(new SessionEntry(MessageRole.User, "hello", DateTimeOffset.UtcNow));

        var session2 = await _sessionManager.GetOrCreateAsync("test:session2", "agent1");

        session2.History.Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveAsync_PersistsHistory()
    {
        var session = await _sessionManager.GetOrCreateAsync("test:persist1", "agent1");
        session.AddEntry(new SessionEntry(MessageRole.User, "user message", DateTimeOffset.UtcNow));
        session.AddEntry(new SessionEntry(MessageRole.Assistant, "assistant reply", DateTimeOffset.UtcNow));

        await _sessionManager.SaveAsync(session);

        // Create a new manager to test actual persistence
        var newManager = new SessionManager(_tempPath, NullLogger<SessionManager>.Instance);
        var loaded = await newManager.GetOrCreateAsync("test:persist1", "agent1");

        loaded.History.Should().HaveCount(2);
        loaded.History[0].Content.Should().Be("user message");
        loaded.History[1].Content.Should().Be("assistant reply");
    }

    [Fact]
    public async Task ResetAsync_ClearsHistory()
    {
        var session = await _sessionManager.GetOrCreateAsync("test:reset1", "agent1");
        session.AddEntry(new SessionEntry(MessageRole.User, "message", DateTimeOffset.UtcNow));
        await _sessionManager.SaveAsync(session);

        await _sessionManager.ResetAsync("test:reset1");

        var reloaded = await _sessionManager.GetOrCreateAsync("test:reset1", "agent1");
        reloaded.History.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var session = await _sessionManager.GetOrCreateAsync("test:delete1", "agent1");
        session.AddEntry(new SessionEntry(MessageRole.User, "message", DateTimeOffset.UtcNow));
        await _sessionManager.SaveAsync(session);

        await _sessionManager.DeleteAsync("test:delete1");

        var newManager = new SessionManager(_tempPath, NullLogger<SessionManager>.Instance);
        var reloaded = await newManager.GetOrCreateAsync("test:delete1", "agent1");
        reloaded.History.Should().BeEmpty();
    }

    [Fact]
    public async Task ListKeysAsync_ReturnsAllSavedSessions()
    {
        var manager = new SessionManager(_tempPath, NullLogger<SessionManager>.Instance);

        for (int i = 1; i <= 3; i++)
        {
            var s = await manager.GetOrCreateAsync($"list:session{i}", "agent");
            s.AddEntry(new SessionEntry(MessageRole.User, "msg", DateTimeOffset.UtcNow));
            await manager.SaveAsync(s);
        }

        var keys = await manager.ListKeysAsync();
        keys.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Session_AddEntry_UpdatesTimestamp()
    {
        var session = new Core.Models.Session { Key = "test", AgentName = "agent" };
        var before = session.UpdatedAt;

        System.Threading.Thread.Sleep(1);
        session.AddEntry(new SessionEntry(MessageRole.User, "msg", DateTimeOffset.UtcNow));

        session.UpdatedAt.Should().BeAfter(before);
    }

    [Fact]
    public void Session_Clear_RemovesAllEntries()
    {
        var session = new Core.Models.Session { Key = "test", AgentName = "agent" };
        session.AddEntry(new SessionEntry(MessageRole.User, "msg1", DateTimeOffset.UtcNow));
        session.AddEntry(new SessionEntry(MessageRole.Assistant, "reply", DateTimeOffset.UtcNow));

        session.Clear();

        session.History.Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
            Directory.Delete(_tempPath, recursive: true);
    }
}
