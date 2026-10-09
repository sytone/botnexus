using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class SubAgentAggregateDedupTests
{
    [Theory]
    [InlineData("sqlite", "spawn_subagent", false)]
    [InlineData("sqlite", "spawn_subagent", true)]
    [InlineData("sqlite", "manage_subagent", false)]
    [InlineData("sqlite", "manage_subagent", true)]
    [InlineData("file", "spawn_subagent", false)]
    [InlineData("file", "spawn_subagent", true)]
    [InlineData("file", "manage_subagent", false)]
    [InlineData("file", "manage_subagent", true)]
    [InlineData("memory", "spawn_subagent", false)]
    [InlineData("memory", "spawn_subagent", true)]
    [InlineData("memory", "manage_subagent", false)]
    [InlineData("memory", "manage_subagent", true)]
    public async Task AddResults_TwoRunsBeforeSave_RetainsBothAndDeduplicatesSameRunRetries(
        string backend, string toolName, bool batch)
    {
        using var fixture = new StoreFixture(backend);
        var store = fixture.Store;
        var parent = await store.GetOrCreateAsync(SessionId.From("aggregate-parent"), AgentId.From("parent"));
        var first = ProjectResult(toolName, "run-a", "result-a");
        var second = ProjectResult(toolName, "run-b", "result-b");
        var firstRetry = ProjectResult(toolName, "run-a", "retry-a-must-not-replace-original");
        var secondRetry = ProjectResult(toolName, "run-b", "retry-b-must-not-replace-original");
        var projections = new[] { first, second, firstRetry, secondRetry };
        projections.ShouldAllBe(row => row.PersistenceKey == "tool-result:reused-provider-id");

        // No save may qualify the provider-only key between the two runs. Fresh retry
        // projections also ensure deduplication is logical, not reference equality.
        if (batch)
            parent.AddEntries(projections);
        else
            foreach (var row in projections) parent.AddEntry(row);

        AssertResults(parent, toolName);
        await store.SaveAsync(parent);
        var reopened = (await fixture.Reopen().GetAsync(parent.SessionId)).ShouldNotBeNull();
        AssertResults(reopened, toolName);
    }

    [Theory]
    [InlineData("file", "spawn_subagent")]
    [InlineData("file", "manage_subagent")]
    [InlineData("memory", "spawn_subagent")]
    [InlineData("memory", "manage_subagent")]
    public async Task Consume_DifferentChildrenReuseProviderIdAcrossRuns_RetainsBothReceiptsAndRejectsCrossRunRetries(
        string backend, string toolName)
    {
        using var fixture = new StoreFixture(backend);
        var store = fixture.Store;
        var parent = await store.GetOrCreateAsync(SessionId.From("receipt-parent"), AgentId.From("parent"));
        await store.SaveAsync(parent);
        for (var index = 0; index < 2; index++)
        {
            await store.SaveSubAgentSessionAsync(new SubAgentInfo
            {
                SubAgentId = $"child-{index}", ParentSessionId = parent.SessionId,
                ChildSessionId = SessionId.From($"child-session-{index}"), ParentAgentId = "parent",
                ChildAgentId = "child", ParentConversationId = parent.ConversationId,
                Task = "task", Status = SubAgentStatus.Completed,
                StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
                ResultSummary = index == 0 ? "result-a" : "result-b"
            });
        }
        var first = ProjectResult(toolName, "run-a", "result-a", "child-0");
        var second = ProjectResult(toolName, "run-b", "result-b", "child-1");
        first.PersistenceKey.ShouldBe(second.PersistenceKey);
        (await store.ConsumeSubAgentResultAsync("child-0", parent.SessionId, parent.ConversationId, first))
            .ShouldBe("result-a");
        (await store.ConsumeSubAgentResultAsync("child-1", parent.SessionId, parent.ConversationId, second))
            .ShouldBe("result-b");

        // File receipts must survive a cold store, while memory receipts must survive
        // another call against the same store. Each retry returns its original payload.
        var reopened = fixture.Reopen();
        foreach (var (child, run, content, otherRun) in new[]
        {
            ("child-0", "run-a", "result-a", "run-b"),
            ("child-1", "run-b", "result-b", "run-a")
        })
        {
            (await reopened.ConsumeSubAgentResultAsync(child, parent.SessionId, parent.ConversationId,
                ProjectResult(toolName, run, "changed-retry-payload", child))).ShouldBe(content);
            (await reopened.ConsumeSubAgentResultAsync(child, parent.SessionId, parent.ConversationId,
                ProjectResult(toolName, otherRun, "wrong-run-payload", child))).ShouldBeNull();
        }

        var loaded = (await reopened.GetAsync(parent.SessionId)).ShouldNotBeNull();
        AssertResults(loaded, toolName);
        // The eventual aggregate save must preserve both authoritative receipts.
        loaded.AddEntries([
            ProjectResult(toolName, "run-a", "later-a", "child-0"),
            ProjectResult(toolName, "run-b", "later-b", "child-1")]);
        await reopened.SaveAsync(loaded);
        var finalStore = fixture.Reopen();
        AssertResults((await finalStore.GetAsync(parent.SessionId)).ShouldNotBeNull(), toolName);
        (await finalStore.ConsumeSubAgentResultAsync("child-0", parent.SessionId, parent.ConversationId, first))
            .ShouldBe("result-a");
        (await finalStore.ConsumeSubAgentResultAsync("child-1", parent.SessionId, parent.ConversationId, second))
            .ShouldBe("result-b");
    }

    private static SessionEntry ProjectResult(string toolName, string run, string content, string child = "child")
        => DefaultToolAuditSink.Instance.ProjectResult("reused-provider-id", toolName, content, false, 4096,
            $"{{\"action\":\"wait\",\"subAgentId\":\"{child}\"}}") with { AgentRunId = AgentRunId.From(run) };

    private static void AssertResults(GatewaySession parent, string toolName)
    {
        var rows = parent.GetHistorySnapshot();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(row => row.Kind == MessageKind.ToolResult && row.ToolName == toolName
            && row.ToolCallId == "reused-provider-id");
        rows.Single(row => row.AgentRunId == AgentRunId.From("run-a")).Content.ShouldBe("result-a");
        rows.Single(row => row.AgentRunId == AgentRunId.From("run-b")).Content.ShouldBe("result-b");
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _backend;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), nameof(SubAgentAggregateDedupTests), Guid.NewGuid().ToString("N"));
        private readonly InMemoryConversationStore _conversations = new();
        private readonly MockFileSystem _fileSystem = new();
        private string DatabasePath => Path.Combine(_directory, "sessions.db");
        public ISessionStore Store { get; }

        public StoreFixture(string backend)
        {
            _backend = backend;
            if (backend == "sqlite") Directory.CreateDirectory(_directory);
            Store = CreateStore();
        }

        public ISessionStore Reopen() => _backend == "memory" ? Store : CreateStore();

        private ISessionStore CreateStore() => _backend switch
        {
            "sqlite" => new SqliteSessionStore($"Data Source={DatabasePath};Pooling=False",
                NullLogger<SqliteSessionStore>.Instance, _conversations),
            "file" => new FileSessionStore(_directory, NullLogger<FileSessionStore>.Instance, _fileSystem, _conversations),
            "memory" => new InMemorySessionStore(null, _conversations),
            _ => throw new ArgumentOutOfRangeException(nameof(_backend))
        };

        public void Dispose()
        {
            if (_backend != "sqlite") return;
            BotNexus.Testing.SqlitePoolCleanup.ClearPoolFor(DatabasePath);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
