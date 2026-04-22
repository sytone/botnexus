using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.CodingAgent.Session;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Session;

public sealed class SessionManagerTests
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), "session-tests");
    private readonly MockFileSystem _fileSystem = new();
    private readonly SessionManager _manager;

    public SessionManagerTests()
    {
        _fileSystem.Directory.CreateDirectory(_workingDirectory);
        _manager = new SessionManager(_fileSystem);
    }

    [Fact]
    public async Task CreateSessionAsync_CreatesSessionMetadata()
    {
        var session = await _manager.CreateSessionAsync(_workingDirectory, "my-session");

        session.Name.ShouldBe("my-session");
        session.WorkingDirectory.ShouldBe(Path.GetFullPath(_workingDirectory));

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        _fileSystem.File.Exists(sessionPath).ShouldBeTrue();

        var lines = await _fileSystem.File.ReadAllLinesAsync(sessionPath);
        lines.Count().ShouldBe(1);
        lines[0].ShouldContain("\"type\":\"session_header\"");
        lines[0].ShouldContain($"\"sessionId\":\"{session.Id}\"");
    }

    [Fact]
    public async Task CreateSessionAsync_WithParentSession_PersistsParentReferenceAndVersion()
    {
        const string parentSessionId = "parent-session-123";
        var session = await _manager.CreateSessionAsync(_workingDirectory, "child-session", parentSessionId);
        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        var header = (await _fileSystem.File.ReadAllLinesAsync(sessionPath)).Single();

        session.ParentSessionId.ShouldBe(parentSessionId);
        session.Version.ShouldBe(2);
        header.ShouldContain("\"type\":\"session_header\"");
        header.ShouldContain("\"version\":2");
        header.ShouldContain($"\"parentSessionId\":\"{parentSessionId}\"");
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
        var fileContent = await _fileSystem.File.ReadAllTextAsync(sessionPath);

        fileContent.ShouldContain("\"type\":\"message\"");
        fileContent.ShouldContain("\"type\":\"tool_result\"");
        fileContent.ShouldContain("\"type\":\"metadata\"");
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
        var fileContent = await _fileSystem.File.ReadAllTextAsync(sessionPath);

        fileContent.ShouldContain("\"type\":\"message\"");
        fileContent.ShouldContain("\"type\":\"compaction_summary\"");
        fileContent.ShouldContain("\"summary\":\"[Session context summary: compacted]\"");
    }

    [Fact]
    public async Task WriteMetadataAsync_PersistsThinkingAndModelChangeEntries()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "metadata-test");
        var withThinking = await _manager.WriteMetadataAsync(created, "thinking_level_change", "off → low");
        await _manager.WriteMetadataAsync(withThinking, "model_change", "gpt-4.1 → claude-sonnet-4.5");

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{created.Id}.jsonl");
        var fileContent = await _fileSystem.File.ReadAllTextAsync(sessionPath);

        fileContent.ShouldContain("\"key\":\"thinking_level_change\"");
        fileContent.ShouldContain("\"value\":\"off \\u2192 low\"");
        fileContent.ShouldContain("\"key\":\"model_change\"");
        fileContent.ShouldContain("\"value\":\"gpt-4.1 \\u2192 claude-sonnet-4.5\"");
    }

    [Fact]
    public async Task WriteMetadataAsync_MetadataEntriesSurviveReload()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "metadata-reload");
        var withMetadata = await _manager.WriteMetadataAsync(created, "thinking_level_change", "low → high");

        await _manager.SaveSessionAsync(withMetadata, [new UserMessage("hello")]);
        var resumed = await _manager.ResumeSessionAsync(created.Id, _workingDirectory);

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{created.Id}.jsonl");
        var fileContent = await _fileSystem.File.ReadAllTextAsync(sessionPath);

        resumed.Messages.ShouldHaveSingleItem();
        fileContent.ShouldContain("\"key\":\"thinking_level_change\"");
        fileContent.ShouldContain("\"value\":\"low \\u2192 high\"");
    }

    [Fact]
    public async Task ResumeSessionAsync_LoadsSessionWithoutMessages()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "resume-only");

        var (session, resumedMessages) = await _manager.ResumeSessionAsync(created.Id, _workingDirectory);

        session.Id.ShouldBe(created.Id);
        resumedMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListSessionsAsync_ReturnsSavedSessions()
    {
        var first = await _manager.CreateSessionAsync(_workingDirectory, "first");
        var second = await _manager.CreateSessionAsync(_workingDirectory, "second");
        await _manager.SaveSessionAsync(first, [new UserMessage("one")]);
        await _manager.SaveSessionAsync(second, [new UserMessage("two"), new SystemAgentMessage("note")]);

        var sessions = await _manager.ListSessionsAsync(_workingDirectory);

        sessions.Count().ShouldBe(2);
        var sessionIds = sessions.Select(session => session.Id).ToList();
        sessionIds.ShouldContain(first.Id);
        sessionIds.ShouldContain(second.Id);
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesSessionFile()
    {
        var session = await _manager.CreateSessionAsync(_workingDirectory, "delete-me");
        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");

        await _manager.DeleteSessionAsync(session.Id, _workingDirectory);

        _fileSystem.File.Exists(sessionPath).ShouldBeFalse();
    }

    [Fact]
    public async Task ListBranchesAndSwitchBranch_WorksForBranchedSession()
    {
        var session = await _manager.CreateSessionAsync(_workingDirectory, "branching");
        await _manager.SaveSessionAsync(session, [new UserMessage("root"), new AssistantAgentMessage("main")]);

        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{session.Id}.jsonl");
        var lines = await _fileSystem.File.ReadAllLinesAsync(sessionPath);
        var rootEntryId = lines
            .Select(line => JsonDocument.Parse(line).RootElement)
            .First(entry => entry.GetProperty("type").GetString() == "message")
            .GetProperty("entryId")
            .GetString();
        rootEntryId.ShouldNotBeNullOrWhiteSpace();

        await _manager.SaveSessionAsync(session with { ActiveLeafId = rootEntryId }, [new UserMessage("root"), new AssistantAgentMessage("branch")]);

        var branches = await _manager.ListBranchesAsync(session.Id, _workingDirectory);
        branches.Count().ShouldBe(2);

        var inactiveBranch = branches.Single(branch => !branch.IsActive);
        var switched = await _manager.SwitchBranchAsync(session.Id, _workingDirectory, inactiveBranch.LeafEntryId, "alternate");
        switched.ActiveLeafId.ShouldBe(inactiveBranch.LeafEntryId);

        var resumed = await _manager.ResumeSessionAsync(session.Id, _workingDirectory);
        resumed.Session.ActiveLeafId.ShouldBe(inactiveBranch.LeafEntryId);
    }

    [Fact]
    public async Task ResumeSessionAsync_LoadsLegacyFlatSession()
    {
        var root = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions");
        _fileSystem.Directory.CreateDirectory(root);
        var sessionId = "legacy-session";
        var legacyDirectory = Path.Combine(root, sessionId);
        _fileSystem.Directory.CreateDirectory(legacyDirectory);

        var metadata = new SessionInfo(
            Id: sessionId,
            Name: "legacy",
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            MessageCount: 1,
            Model: null,
            WorkingDirectory: Path.GetFullPath(_workingDirectory));
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(legacyDirectory, "session.json"), JsonSerializer.Serialize(metadata));

        var userPayload = JsonSerializer.SerializeToElement(new UserMessage("legacy hello"));
        await _fileSystem.File.WriteAllTextAsync(
            Path.Combine(legacyDirectory, "messages.jsonl"),
            JsonSerializer.Serialize(new { Type = "user", Payload = userPayload }) + Environment.NewLine);

        var resumed = await _manager.ResumeSessionAsync(sessionId, _workingDirectory);
        resumed.Messages.ShouldHaveSingleItem();
        resumed.Messages[0].ShouldBeOfType<UserMessage>();
        ((UserMessage)resumed.Messages[0]).Content.ShouldBe("legacy hello");
    }

    [Fact]
    public async Task SaveSessionAsync_UpgradesOlderSessionHeaderVersion()
    {
        var created = await _manager.CreateSessionAsync(_workingDirectory, "upgrade-version");
        var sessionPath = Path.Combine(_workingDirectory, ".botnexus-agent", "sessions", $"{created.Id}.jsonl");
        var lines = await _fileSystem.File.ReadAllLinesAsync(sessionPath);
        lines[0] = lines[0].Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal);
        await _fileSystem.File.WriteAllLinesAsync(sessionPath, lines);

        var resumed = await _manager.ResumeSessionAsync(created.Id, _workingDirectory);
        resumed.Session.Version.ShouldBe(1);

        await _manager.SaveSessionAsync(resumed.Session, [new UserMessage("after-upgrade")]);

        var headers = (await _fileSystem.File.ReadAllLinesAsync(sessionPath))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(entry => entry.GetProperty("type").GetString() == "session_header")
            .ToList();
        headers.ShouldNotBeEmpty();
        headers.Last().GetProperty("version").GetInt32().ShouldBe(2);
    }
}
