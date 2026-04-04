using BotNexus.Core.Models;
using BotNexus.Session;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class SessionHideTests : IDisposable
{
    private readonly string _tempPath;
    private readonly SessionManager _sessionManager;

    public SessionHideTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"botnexus-hide-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempPath);
        _sessionManager = new SessionManager(_tempPath, NullLogger<SessionManager>.Instance);
    }

    [Fact]
    public async Task SetHiddenAsync_NewSession_CreatesMetadata()
    {
        // Session must exist before metadata can be set
        var session = await _sessionManager.GetOrCreateAsync("test:hide1", "agent1");
        await _sessionManager.SaveAsync(session);

        await _sessionManager.SetHiddenAsync("test:hide1", true);

        var isHidden = await _sessionManager.IsHiddenAsync("test:hide1");
        isHidden.Should().BeTrue();
    }

    [Fact]
    public async Task SetHiddenAsync_ExistingSession_UpdatesMetadata()
    {
        var session = await _sessionManager.GetOrCreateAsync("test:hide2", "agent1");
        session.AddEntry(new SessionEntry(MessageRole.User, "message", DateTimeOffset.UtcNow));
        await _sessionManager.SaveAsync(session);

        await _sessionManager.SetHiddenAsync("test:hide2", true);

        var isHidden = await _sessionManager.IsHiddenAsync("test:hide2");
        isHidden.Should().BeTrue();
    }

    [Fact]
    public async Task SetHiddenAsync_ToggleHidden_WorksCorrectly()
    {
        var session = await _sessionManager.GetOrCreateAsync("test:toggle1", "agent1");
        await _sessionManager.SaveAsync(session);

        await _sessionManager.SetHiddenAsync("test:toggle1", true);
        var hidden = await _sessionManager.IsHiddenAsync("test:toggle1");
        hidden.Should().BeTrue();

        await _sessionManager.SetHiddenAsync("test:toggle1", false);
        var visible = await _sessionManager.IsHiddenAsync("test:toggle1");
        visible.Should().BeFalse();
    }

    [Fact]
    public async Task IsHiddenAsync_NonExistentSession_ReturnsFalse()
    {
        var isHidden = await _sessionManager.IsHiddenAsync("test:nonexistent");
        isHidden.Should().BeFalse();
    }

    [Fact]
    public async Task IsHiddenAsync_SessionWithoutMetadata_ReturnsFalse()
    {
        var session = await _sessionManager.GetOrCreateAsync("test:no-meta", "agent1");
        await _sessionManager.SaveAsync(session);

        var isHidden = await _sessionManager.IsHiddenAsync("test:no-meta");
        isHidden.Should().BeFalse();
    }

    [Fact]
    public async Task SetHiddenAsync_PreservesAgentName()
    {
        var session = await _sessionManager.GetOrCreateAsync("test:preserve1", "custom-agent");
        await _sessionManager.SaveAsync(session);

        await _sessionManager.SetHiddenAsync("test:preserve1", true);

        var newManager = new SessionManager(_tempPath, NullLogger<SessionManager>.Instance);
        var reloaded = await newManager.GetOrCreateAsync("test:preserve1", "custom-agent");

        reloaded.AgentName.Should().Be("custom-agent");
    }

    [Fact]
    public async Task SetHiddenAsync_MultipleSessionsConcurrently_AreThreadSafe()
    {
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var sessionKey = $"test:concurrent{i}";
            var session = await _sessionManager.GetOrCreateAsync(sessionKey, "agent1");
            await _sessionManager.SaveAsync(session);
            await _sessionManager.SetHiddenAsync(sessionKey, i % 2 == 0);
        });

        await Task.WhenAll(tasks);

        for (int i = 0; i < 10; i++)
        {
            var isHidden = await _sessionManager.IsHiddenAsync($"test:concurrent{i}");
            isHidden.Should().Be(i % 2 == 0);
        }
    }

    [Fact]
    public async Task SetHiddenAsync_PersistsAcrossManagerInstances()
    {
        // Session must exist before setting hidden
        var session = await _sessionManager.GetOrCreateAsync("test:persist1", "agent1");
        await _sessionManager.SaveAsync(session);

        await _sessionManager.SetHiddenAsync("test:persist1", true);

        var newManager = new SessionManager(_tempPath, NullLogger<SessionManager>.Instance);
        var isHidden = await newManager.IsHiddenAsync("test:persist1");

        isHidden.Should().BeTrue();
    }

    [Fact]
    public async Task SetHiddenAsync_DoesNotAffectSessionHistory()
    {
        var session = await _sessionManager.GetOrCreateAsync("test:history1", "agent1");
        session.AddEntry(new SessionEntry(MessageRole.User, "message 1", DateTimeOffset.UtcNow));
        session.AddEntry(new SessionEntry(MessageRole.Assistant, "response 1", DateTimeOffset.UtcNow));
        await _sessionManager.SaveAsync(session);

        await _sessionManager.SetHiddenAsync("test:history1", true);

        var reloaded = await _sessionManager.GetOrCreateAsync("test:history1", "agent1");
        reloaded.History.Should().HaveCount(2);
        reloaded.History[0].Content.Should().Be("message 1");
    }

    [Fact]
    public async Task IsHiddenAsync_MalformedMetadata_ReturnsFalse()
    {
        var sessionKey = "test:malformed";
        var session = await _sessionManager.GetOrCreateAsync(sessionKey, "agent1");
        await _sessionManager.SaveAsync(session);

        var encodedKey = EncodeSessionKey(sessionKey);
        var metaPath = Path.Combine(_tempPath, $"{encodedKey}.meta.json");
        await File.WriteAllTextAsync(metaPath, "{ invalid json }");

        var isHidden = await _sessionManager.IsHiddenAsync(sessionKey);
        isHidden.Should().BeFalse();
    }

    [Fact]
    public async Task SetHiddenAsync_SpecialCharactersInSessionKey_HandledCorrectly()
    {
        var sessionKey = "discord:user@123:chat#456";
        var session = await _sessionManager.GetOrCreateAsync(sessionKey, "agent1");
        await _sessionManager.SaveAsync(session);

        await _sessionManager.SetHiddenAsync(sessionKey, true);

        var isHidden = await _sessionManager.IsHiddenAsync(sessionKey);
        isHidden.Should().BeTrue();
    }

    [Fact]
    public async Task SetHiddenAsync_CalledMultipleTimes_LastValueWins()
    {
        // Session must exist before setting hidden
        var session = await _sessionManager.GetOrCreateAsync("test:multiple", "agent1");
        await _sessionManager.SaveAsync(session);

        await _sessionManager.SetHiddenAsync("test:multiple", true);
        await _sessionManager.SetHiddenAsync("test:multiple", false);
        await _sessionManager.SetHiddenAsync("test:multiple", true);

        var isHidden = await _sessionManager.IsHiddenAsync("test:multiple");
        isHidden.Should().BeTrue();
    }

    private static string EncodeSessionKey(string key)
        => Uri.EscapeDataString(key).Replace("%", "_");

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
            Directory.Delete(_tempPath, recursive: true);
    }
}
