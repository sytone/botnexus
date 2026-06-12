using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Memory.Models;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Abstractions.Agents;

namespace BotNexus.Memory.Tests;

public sealed class MemoryIndexerDelegationTests
{
    [Fact]
    public void BuildSessionEvent_MapsHistoryToTurns()
    {
        var session = new GatewaySession { SessionId = SessionId.From("session-1"), AgentId = AgentId.From("test-agent") };
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "Hello", Timestamp = DateTimeOffset.UtcNow });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "Hi there", Timestamp = DateTimeOffset.UtcNow });

        var result = MemoryIndexer.BuildSessionEvent(session, "test-agent", "session-1");

        result.AgentId.ShouldBe("test-agent");
        result.SessionId.ShouldBe("session-1");
        result.History.ShouldNotBeNull();
        result.History!.Count.ShouldBe(2);
        result.History[0].Role.ShouldBe("user");
        result.History[0].Content.ShouldBe("Hello");
        result.History[1].Role.ShouldBe("assistant");
        result.History[1].Content.ShouldBe("Hi there");
    }

    [Fact]
    public async Task MarkdownAgentMemory_OnSessionComplete_IndexesTurnPairs()
    {
        var store = new InMemoryMemoryStore();
        var fileSystem = new MockFileSystem();
        var workspaceManager = new StubWorkspaceManager("/workspace");
        var memory = new MarkdownAgentMemory("test-agent", workspaceManager, store, fileSystem);

        var history = new List<AgentMemorySessionTurn>
        {
            new(0, "user", "What is 2+2?", DateTimeOffset.UtcNow),
            new(1, "assistant", "4", DateTimeOffset.UtcNow),
            new(2, "user", "Thanks", DateTimeOffset.UtcNow),
            new(3, "assistant", "You're welcome", DateTimeOffset.UtcNow)
        };

        var sessionEvent = new AgentMemorySessionEvent(
            "test-agent", "session-1", TurnCount: 4, History: history);

        await memory.OnSessionCompleteAsync(sessionEvent);

        var indexed = await store.GetBySessionAsync("session-1", 100);
        indexed.Count.ShouldBe(2);
        indexed[0].Content.ShouldContain("What is 2+2?");
        indexed[0].Content.ShouldContain("4");
        indexed[1].Content.ShouldContain("Thanks");
        indexed[1].Content.ShouldContain("You're welcome");
    }

    [Fact]
    public async Task MarkdownAgentMemory_OnSessionComplete_SkipsToolMessages()
    {
        var store = new InMemoryMemoryStore();
        var fileSystem = new MockFileSystem();
        var workspaceManager = new StubWorkspaceManager("/workspace");
        var memory = new MarkdownAgentMemory("test-agent", workspaceManager, store, fileSystem);

        var history = new List<AgentMemorySessionTurn>
        {
            new(0, "user", "Read file.txt", DateTimeOffset.UtcNow),
            new(1, "tool", "file contents here", DateTimeOffset.UtcNow),
            new(2, "assistant", "Here is the file content", DateTimeOffset.UtcNow)
        };

        var sessionEvent = new AgentMemorySessionEvent(
            "test-agent", "session-2", TurnCount: 3, History: history);

        await memory.OnSessionCompleteAsync(sessionEvent);

        var indexed = await store.GetBySessionAsync("session-2", 100);
        indexed.Count.ShouldBe(1);
        indexed[0].Content.ShouldContain("Read file.txt");
        indexed[0].Content.ShouldContain("Here is the file content");
        indexed[0].Content.ShouldNotContain("file contents here");
    }

    [Fact]
    public async Task MarkdownAgentMemory_OnSessionComplete_DeduplicatesExistingTurns()
    {
        var store = new InMemoryMemoryStore();
        var fileSystem = new MockFileSystem();
        var workspaceManager = new StubWorkspaceManager("/workspace");
        var memory = new MarkdownAgentMemory("test-agent", workspaceManager, store, fileSystem);

        // Pre-index turn 0
        await store.InsertAsync(new MemoryEntry
        {
            Id = "existing",
            AgentId = "test-agent",
            SessionId = "session-3",
            TurnIndex = 0,
            SourceType = "conversation",
            Content = "User: Hello\nAssistant: Hi",
            CreatedAt = DateTimeOffset.UtcNow
        });

        var history = new List<AgentMemorySessionTurn>
        {
            new(0, "user", "Hello", DateTimeOffset.UtcNow),
            new(1, "assistant", "Hi", DateTimeOffset.UtcNow),
            new(2, "user", "Second question", DateTimeOffset.UtcNow),
            new(3, "assistant", "Second answer", DateTimeOffset.UtcNow)
        };

        var sessionEvent = new AgentMemorySessionEvent(
            "test-agent", "session-3", TurnCount: 4, History: history);

        await memory.OnSessionCompleteAsync(sessionEvent);

        var indexed = await store.GetBySessionAsync("session-3", 100);
        // Should have 2: the pre-existing one + the new turn pair (not a duplicate of turn 0)
        indexed.Count.ShouldBe(2);
    }

    [Fact]
    public async Task MarkdownAgentMemory_OnSessionComplete_WithNullHistory_DoesNothing()
    {
        var store = new InMemoryMemoryStore();
        var fileSystem = new MockFileSystem();
        var workspaceManager = new StubWorkspaceManager("/workspace");
        var memory = new MarkdownAgentMemory("test-agent", workspaceManager, store, fileSystem);

        var sessionEvent = new AgentMemorySessionEvent("test-agent", "session-4", TurnCount: 0);

        await memory.OnSessionCompleteAsync(sessionEvent);

        var indexed = await store.GetBySessionAsync("session-4", 100);
        indexed.Count.ShouldBe(0);
    }

    private sealed class StubWorkspaceManager : IAgentWorkspaceManager
    {
        private readonly string _path;
        public StubWorkspaceManager(string path) => _path = path;
        public string GetWorkspacePath(string agentName) => _path;
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: "", Identity: "", User: "", Memory: ""));
        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryMemoryStore : IMemoryStore
    {
        private readonly List<MemoryEntry> _entries = [];
        private int _nextId = 1;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default)
        {
            entry = entry with { Id = (_nextId++).ToString() };
            _entries.Add(entry);
            return Task.FromResult(entry);
        }

        public Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>(_entries.Where(e => e.SessionId == sessionId).Take(limit).ToList());

        public Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK, MemorySearchFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>(_entries.Take(topK).ToList());

        public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new MemoryStoreStats(_entries.Count, 0, null));

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
