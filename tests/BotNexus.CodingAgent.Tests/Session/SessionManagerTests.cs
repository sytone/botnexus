using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Session;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Session;

public sealed class SessionManagerTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-session-{Guid.NewGuid():N}");
    private readonly SessionManager _manager = new();

    public SessionManagerTests()
    {
        Directory.CreateDirectory(_workingDirectory);
    }

    [Fact]
    public async Task CreateSessionAsync_CreatesSessionMetadata()
    {
        var session = await _manager.CreateSessionAsync(_workingDirectory, "my-session");

        session.Name.Should().Be("my-session");
        session.WorkingDirectory.Should().Be(Path.GetFullPath(_workingDirectory));
        var sessionDirectory = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", session.Id);
        File.Exists(Path.Combine(sessionDirectory, "session.json")).Should().BeTrue();
        File.Exists(Path.Combine(sessionDirectory, "messages.jsonl")).Should().BeTrue();
    }

    [Fact]
    public async Task SaveSessionAsync_WritesMessagesFile()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "resume-test");
        var messages = new AgentMessage[]
        {
            new UserMessage("hello"),
            new SystemAgentMessage("session metadata")
        };

        await _manager.SaveSessionAsync(created, messages);
        var messagesPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", created.Id, "messages.jsonl");
        var fileContent = await File.ReadAllTextAsync(messagesPath);

        fileContent.Should().Contain("\"Type\": \"user\"");
        fileContent.Should().Contain("\"Type\": \"system\"");
        fileContent.Should().Contain("\"Content\": \"hello\"");
    }

    [Fact]
    public async Task ResumeSessionAsync_LoadsSessionWithoutMessages()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "resume-only");

        var (session, resumedMessages) = await _manager.ResumeSessionAsync(created.Id, _workingDirectory);

        session.Id.Should().Be(created.Id);
        resumedMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSessionsAsync_ReturnsSavedSessions()
    {
        var first = await _manager.CreateSessionAsync(_workingDirectory, "first");
        var second = await _manager.CreateSessionAsync(_workingDirectory, "second");
        await _manager.SaveSessionAsync(first, [new UserMessage("one")]);
        await _manager.SaveSessionAsync(second, [new UserMessage("two"), new SystemAgentMessage("note")]);

        var sessions = await _manager.ListSessionsAsync(_workingDirectory);

        sessions.Should().HaveCount(2);
        sessions.Select(session => session.Id).Should().Contain([first.Id, second.Id]);
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesSessionDirectory()
    {
        var session = await _manager.CreateSessionAsync(_workingDirectory, "delete-me");
        var sessionDirectory = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", session.Id);

        await _manager.DeleteSessionAsync(session.Id, _workingDirectory);

        Directory.Exists(sessionDirectory).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
