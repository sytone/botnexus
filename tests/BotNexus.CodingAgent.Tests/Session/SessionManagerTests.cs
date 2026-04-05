using System.Text.Json;
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

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        File.Exists(sessionPath).Should().BeTrue();

        var lines = await File.ReadAllLinesAsync(sessionPath);
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("\"type\":\"session_header\"");
        lines[0].Should().Contain($"\"sessionId\":\"{session.Id}\"");
    }

    [Fact]
    public async Task CreateSessionAsync_WithParentSession_PersistsParentReferenceAndVersion()
    {
        const string parentSessionId = "parent-session-123";
        var session = await _manager.CreateSessionAsync(_workingDirectory, "child-session", parentSessionId);
        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        var header = (await File.ReadAllLinesAsync(sessionPath)).Single();

        session.ParentSessionId.Should().Be(parentSessionId);
        session.Version.Should().Be(2);
        header.Should().Contain("\"type\":\"session_header\"");
        header.Should().Contain("\"version\":2");
        header.Should().Contain($"\"parentSessionId\":\"{parentSessionId}\"");
    }

    [Fact]
    public async Task SaveSessionAsync_WritesTypedEntries()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "resume-test");
        var messages = new AgentMessage[]
        {
            new UserMessage("hello"),
            new ToolResultAgentMessage("tc-1", "shell", new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]))
        };

        await _manager.SaveSessionAsync(created, messages);

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{created.Id}.jsonl");
        var fileContent = await File.ReadAllTextAsync(sessionPath);

        fileContent.Should().Contain("\"type\":\"message\"");
        fileContent.Should().Contain("\"type\":\"tool_result\"");
        fileContent.Should().Contain("\"type\":\"metadata\"");
    }

    [Fact]
    public async Task SaveSessionAsync_WritesCompactionSummaryEntryType()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "compaction-test");
        var messages = new AgentMessage[]
        {
            new UserMessage("hello"),
            new SystemAgentMessage("[Session context summary: compacted]")
        };

        await _manager.SaveSessionAsync(created, messages);

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{created.Id}.jsonl");
        var fileContent = await File.ReadAllTextAsync(sessionPath);

        fileContent.Should().Contain("\"type\":\"message\"");
        fileContent.Should().Contain("\"type\":\"compaction_summary\"");
        fileContent.Should().Contain("\"summary\":\"[Session context summary: compacted]\"");
    }

    [Fact]
    public async Task WriteMetadataAsync_PersistsThinkingAndModelChangeEntries()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "metadata-test");
        var withThinking = await _manager.WriteMetadataAsync(created, "thinking_level_change", "off → low");
        await _manager.WriteMetadataAsync(withThinking, "model_change", "gpt-4.1 → claude-sonnet-4.5");

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{created.Id}.jsonl");
        var fileContent = await File.ReadAllTextAsync(sessionPath);

        fileContent.Should().Contain("\"key\":\"thinking_level_change\"");
        fileContent.Should().Contain("\"value\":\"off \\u2192 low\"");
        fileContent.Should().Contain("\"key\":\"model_change\"");
        fileContent.Should().Contain("\"value\":\"gpt-4.1 \\u2192 claude-sonnet-4.5\"");
    }

    [Fact]
    public async Task WriteMetadataAsync_MetadataEntriesSurviveReload()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "metadata-reload");
        var withMetadata = await _manager.WriteMetadataAsync(created, "thinking_level_change", "low → high");

        await _manager.SaveSessionAsync(withMetadata, [new UserMessage("hello")]);
        var resumed = await _manager.ResumeSessionAsync(created.Id, _workingDirectory);

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{created.Id}.jsonl");
        var fileContent = await File.ReadAllTextAsync(sessionPath);

        resumed.Messages.Should().ContainSingle();
        fileContent.Should().Contain("\"key\":\"thinking_level_change\"");
        fileContent.Should().Contain("\"value\":\"low \\u2192 high\"");
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
    public async Task DeleteSessionAsync_RemovesSessionFile()
    {
        var session = await _manager.CreateSessionAsync(_workingDirectory, "delete-me");
        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");

        await _manager.DeleteSessionAsync(session.Id, _workingDirectory);

        File.Exists(sessionPath).Should().BeFalse();
    }

    [Fact]
    public async Task ListBranchesAndSwitchBranch_WorksForBranchedSession()
    {
        var session = await _manager.CreateSessionAsync(_workingDirectory, "branching");
        await _manager.SaveSessionAsync(session, [new UserMessage("root"), new AssistantAgentMessage("main")]);

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        var lines = await File.ReadAllLinesAsync(sessionPath);
        var rootEntryId = JsonDocument.Parse(lines[1]).RootElement.GetProperty("entryId").GetString();
        rootEntryId.Should().NotBeNullOrWhiteSpace();

        await _manager.SaveSessionAsync(session with { ActiveLeafId = rootEntryId }, [new UserMessage("root"), new AssistantAgentMessage("branch")]);

        var branches = await _manager.ListBranchesAsync(session.Id, _workingDirectory);
        branches.Should().HaveCount(2);

        var inactiveBranch = branches.Single(branch => !branch.IsActive);
        var switched = await _manager.SwitchBranchAsync(session.Id, _workingDirectory, inactiveBranch.LeafEntryId, "alternate");
        switched.ActiveLeafId.Should().Be(inactiveBranch.LeafEntryId);

        var resumed = await _manager.ResumeSessionAsync(session.Id, _workingDirectory);
        resumed.Session.ActiveLeafId.Should().Be(inactiveBranch.LeafEntryId);
    }

    [Fact]
    public async Task ResumeSessionAsync_LoadsLegacyFlatSession()
    {
        var root = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions");
        Directory.CreateDirectory(root);
        var sessionId = "legacy-session";
        var legacyDirectory = Path.Combine(root, sessionId);
        Directory.CreateDirectory(legacyDirectory);

        var metadata = new SessionInfo(
            Id: sessionId,
            Name: "legacy",
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            MessageCount: 1,
            Model: null,
            WorkingDirectory: Path.GetFullPath(_workingDirectory));
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "session.json"), JsonSerializer.Serialize(metadata));

        var userPayload = JsonSerializer.SerializeToElement(new UserMessage("legacy hello"));
        await File.WriteAllTextAsync(
            Path.Combine(legacyDirectory, "messages.jsonl"),
            JsonSerializer.Serialize(new { Type = "user", Payload = userPayload }) + Environment.NewLine);

        var resumed = await _manager.ResumeSessionAsync(sessionId, _workingDirectory);
        resumed.Messages.Should().ContainSingle();
        resumed.Messages[0].Should().BeOfType<UserMessage>();
        ((UserMessage)resumed.Messages[0]).Content.Should().Be("legacy hello");
    }

    [Fact]
    public async Task SaveSessionAsync_UpgradesOlderSessionHeaderVersion()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "upgrade-version");
        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{created.Id}.jsonl");
        var lines = await File.ReadAllLinesAsync(sessionPath);
        lines[0] = lines[0].Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(sessionPath, lines);

        var resumed = await _manager.ResumeSessionAsync(created.Id, _workingDirectory);
        resumed.Session.Version.Should().Be(1);

        await _manager.SaveSessionAsync(resumed.Session, [new UserMessage("after-upgrade")]);

        var updatedHeader = (await File.ReadAllLinesAsync(sessionPath)).First();
        updatedHeader.Should().Contain("\"version\":2");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }
}
