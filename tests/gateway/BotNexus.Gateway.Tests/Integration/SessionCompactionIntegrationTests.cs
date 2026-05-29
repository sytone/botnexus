using BotNexus.Domain.Primitives;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class SessionCompactionIntegrationTests : IDisposable
{
    private static readonly LlmModel TestModel = new(
        Id: "test-model",
        Name: "Test Model",
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 32000,
        MaxTokens: 4096);

    private readonly List<string> _cleanupDirectories = [];

    [Fact]
    public async Task CompactedSession_PersistsAcrossStoreRecreation()
    {
        using var fixture = new MockFileStoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("compaction-persist"), AgentId.From("agent-a"));

        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"before-{i}" });

        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "compaction-summary",
            IsCompactionSummary = true
        });

        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = $"after-{i}" });

        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("compaction-persist"));

        // Phase 3a (#531): full transcript preserved; single active summary stays LLM-visible.
        reloaded.ShouldNotBeNull();
        reloaded!.History.Count.ShouldBe(21);
        reloaded.History.Single(e => e.IsCompactionSummary).Content.ShouldBe("compaction-summary");
    }

    [Fact]
    public async Task MultipleCompactions_OnlyLatestSummaryRemainsLlmVisible()
    {
        using var fixture = new MockFileStoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("multi-compaction"), AgentId.From("agent-a"));

        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "start-1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "start-2" },
            new SessionEntry { Role = MessageRole.System, Content = "summary-1", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "middle-1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "middle-2" },
            new SessionEntry { Role = MessageRole.System, Content = "summary-2", IsCompactionSummary = true },
            new SessionEntry { Role = MessageRole.User, Content = "tail-1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "tail-2" }
        ]);

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("multi-compaction"));

        // Phase 3a (#531): legacy multi-summary state is forward-migrated — all-but-latest summary marked IsHistory.
        reloaded.ShouldNotBeNull();
        reloaded!.History.Count.ShouldBe(8);
        reloaded.History.Single(e => e.Content == "summary-1").IsHistory.ShouldBeTrue();
        reloaded.History.Single(e => e.Content == "summary-2").IsHistory.ShouldBeFalse();
        reloaded.History.Single(e => e.Content == "summary-2").IsCompactionSummary.ShouldBeTrue();
    }

    [Fact]
    public async Task CompactedSession_OriginalEntriesStillOnDisk()
    {
        var storePath = CreateStorePath();
        var sessionId = "raw-disk";
        var store = new FileSessionStore(storePath, NullLogger<FileSessionStore>.Instance, new FileSystem());
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From("agent-a"));

        for (var i = 0; i < 6; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"pre-{i}" });

        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "summary-on-disk",
            IsCompactionSummary = true
        });

        for (var i = 0; i < 4; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = $"post-{i}" });

        await store.SaveAsync(session);

        var encodedName = Uri.EscapeDataString(sessionId);
        var historyPath = Path.Combine(storePath, $"{encodedName}.jsonl");
        var lines = await File.ReadAllLinesAsync(historyPath);

        lines.Count().ShouldBe(11);
        lines.Count(line => line.Contains("\"isCompactionSummary\":true", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    [Fact]
    public async Task CompactedSession_SqliteStore_SameLoadBehavior()
    {
        using var fixture = new SqliteStoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync(SessionId.From("sqlite-compaction"), AgentId.From("agent-a"));

        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"before-{i}" });

        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "sqlite-summary",
            IsCompactionSummary = true
        });

        for (var i = 0; i < 10; i++)
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = $"after-{i}" });

        await store.SaveAsync(session);
        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("sqlite-compaction"));

        // Phase 3a (#531): full transcript preserved through SQLite roundtrip.
        reloaded.ShouldNotBeNull();
        reloaded!.History.Count.ShouldBe(21);
        reloaded.History.Single(e => e.IsCompactionSummary).Content.ShouldBe("sqlite-summary");
    }

    [Fact]
    public async Task LlmSessionCompactor_SplitHistory_PreservesExactTurns()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("split-preserve"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var originalEntries = new List<SessionEntry>();
        for (var turn = 1; turn <= 10; turn++)
        {
            originalEntries.Add(new SessionEntry { Role = MessageRole.User, Content = $"user-{turn}" });
            originalEntries.Add(new SessionEntry { Role = MessageRole.Assistant, Content = $"assistant-{turn}" });
        }
        session.AddEntries(originalEntries);

        var compactor = CreateCompactor("fixed-summary");
        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 3,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        session.ReplaceHistory(result.CompactedHistory!);

        var history = session.GetHistorySnapshot();
        // Phase 3a (#531): original 20 entries preserved + 1 summary appended at boundary = 21 total.
        history.Count.ShouldBe(21);
        // Summarised entries (14) marked historical, then summary, then 6 preserved.
        history.Take(14).ShouldAllBe(e => e.IsHistory);
        history[14].IsCompactionSummary.ShouldBeTrue();
        history[14].IsHistory.ShouldBeFalse();
        history.Skip(15).Select(entry => entry.Content)
            .ShouldBe(new[] { "user-8", "assistant-8", "user-9", "assistant-9", "user-10", "assistant-10" }, ignoreOrder: false);
    }

    [Fact]
    public async Task LlmSessionCompactor_WithToolEntries_GroupsCorrectly()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("tool-grouping"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "u1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a1" },
            new SessionEntry { Role = MessageRole.User, Content = "u2" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a2" },
            new SessionEntry { Role = MessageRole.Tool, Content = "tool-call", ToolName = "search", ToolCallId = "1" },
            new SessionEntry { Role = MessageRole.Tool, Content = "tool-result", ToolName = "search", ToolCallId = "1" }
        ]);

        var compactor = CreateCompactor("tool-summary");
        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        session.ReplaceHistory(result.CompactedHistory!);

        // Phase 3a (#531): summarised entries preserved as historical; summary at boundary; preserved tail after.
        session.GetHistorySnapshot().Select(entry => entry.Content)
            .ShouldBe(new[] { "u1", "a1", "tool-summary", "u2", "a2", "tool-call", "tool-result" }, ignoreOrder: false);
    }

    [Fact]
    public async Task CompactThenSendMessage_HistoryIsCoherent()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("compact-send"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "u1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a1" },
            new SessionEntry { Role = MessageRole.User, Content = "u2" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a2" }
        ]);

        var compactor = CreateCompactor("coherent-summary");
        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        session.ReplaceHistory(result.CompactedHistory!);

        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u3" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "a3" });

        // Phase 3a (#531): full transcript including newly-historical entries remains coherent.
        session.GetHistorySnapshot().Select(entry => entry.Content)
            .ShouldBe(new[] { "u1", "a1", "coherent-summary", "u2", "a2", "u3", "a3" }, ignoreOrder: false);
    }

    [Fact]
    public async Task MultiCycleCompaction_PreservesAllOriginalTurns_InStore()
    {
        // Phase 3a (#531): the canonical promise. After N compaction cycles every
        // originally-inserted turn must still exist in the session store — only the
        // LLM-visible projection shrinks via IsHistory marking.
        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("multi-cycle"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a")
        };

        var insertedContents = new List<string>();
        for (var i = 1; i <= 4; i++)
        {
            var u = $"cycle0-u{i}";
            var a = $"cycle0-a{i}";
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = u });
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = a });
            insertedContents.Add(u);
            insertedContents.Add(a);
        }

        // Cycle 1.
        var compactor1 = CreateCompactor("summary-c1");
        var result1 = await compactor1.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });
        result1.Succeeded.ShouldBeTrue();
        session.ReplaceHistory(result1.CompactedHistory!);

        // Add more turns to drive cycle 2.
        for (var i = 1; i <= 4; i++)
        {
            var u = $"cycle1-u{i}";
            var a = $"cycle1-a{i}";
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = u });
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = a });
            insertedContents.Add(u);
            insertedContents.Add(a);
        }

        // Cycle 2.
        var compactor2 = CreateCompactor("summary-c2");
        var result2 = await compactor2.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });
        result2.Succeeded.ShouldBeTrue();
        session.ReplaceHistory(result2.CompactedHistory!);

        var snapshot = session.GetHistorySnapshot();

        // Every originally-inserted turn is still present.
        foreach (var content in insertedContents)
            snapshot.Select(e => e.Content).ShouldContain(content,
                $"original turn '{content}' must survive across compaction cycles");

        // Two summaries on disk; older one is historical.
        snapshot.Single(e => e.Content == "summary-c1").IsHistory.ShouldBeTrue();
        snapshot.Single(e => e.Content == "summary-c2").IsHistory.ShouldBeFalse();
        snapshot.Single(e => e.Content == "summary-c2").IsCompactionSummary.ShouldBeTrue();
    }

    [Fact]
    public async Task CompactThenArchive_BothOperationsWork()
    {
        var storePath = CreateStorePath();
        const string sessionId = "compact-archive";
        var encodedSessionId = Uri.EscapeDataString(sessionId);
        var store = new FileSessionStore(storePath, NullLogger<FileSessionStore>.Instance, new FileSystem());
        var session = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From("agent-a"));
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "u1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a1" },
            new SessionEntry { Role = MessageRole.User, Content = "u2" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a2" }
        ]);

        var compactor = CreateCompactor("archived-summary");
        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        if (result.Succeeded && result.CompactedHistory is not null)
            session.ReplaceHistory(result.CompactedHistory);

        await store.SaveAsync(session);

        await store.ArchiveAsync(SessionId.From(sessionId));
        var newSession = await store.GetOrCreateAsync(SessionId.From(sessionId), AgentId.From("agent-a"));

        Directory.GetFiles(storePath, $"{encodedSessionId}.jsonl.archived.*").ShouldHaveSingleItem();
        Directory.GetFiles(storePath, $"{encodedSessionId}.meta.json.archived.*").ShouldHaveSingleItem();
        newSession.History.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CompactionSummary_ExceedsMaxChars_IsTruncated()
    {
        var session = BuildCompactionSession();
        var compactor = CreateCompactor(new string('x', 50_000));

        var result = await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            MaxSummaryChars = 16_000,
            SummarizationModel = TestModel.Id
        });

        result.Succeeded.ShouldBeTrue();
        result.CompactedHistory.ShouldNotBeNull();
        session.ReplaceHistory(result.CompactedHistory!);

        session.GetHistorySnapshot().First().Content.Length.ShouldBeLessThanOrEqualTo(16_000);
    }

    /// <summary>
    /// Regression test for Bug 1 (#366): when the LLM returns an empty summary, compaction
    /// must abort and leave history completely unchanged — no data loss.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task CompactionSummary_EmptyResponse_HandledGracefully()
    {
        var session = BuildCompactionSession();
        var originalCount = session.GetHistorySnapshot().Count;
        var originalContents = session.GetHistorySnapshot().Select(e => e.Content).ToList();
        var compactor = CreateCompactor(string.Empty);

        Func<Task> act = async () => await compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        await act.ShouldNotThrowAsync();

        // Bug 1 fix: history must be completely unchanged when LLM returns empty summary
        session.GetHistorySnapshot().Count().ShouldBe(originalCount,
            "history must not be modified when LLM returns empty summary");
        session.GetHistorySnapshot().Select(e => e.Content).ToList()
            .ShouldBe(originalContents, "no entries should be lost when compaction is aborted");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ShouldCompact_EmptySession_ReturnsFalse()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("empty"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        var compactor = CreateCompactor("unused");

        compactor.ShouldCompact(session.Session, new CompactionOptions()).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ShouldCompact_SessionWithOnlyCompactionEntry_ReturnsFalse()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("only-summary"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true });
        var compactor = CreateCompactor("unused");

        compactor.ShouldCompact(session.Session, new CompactionOptions()).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CompactAsync_ConcurrentAccess_NoCorruption()
    {
        var session = new GatewaySession { SessionId = BotNexus.Domain.Primitives.SessionId.From("concurrent"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        for (var i = 0; i < 12; i++)
        {
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"u-{i}" });
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = $"a-{i}" });
        }

        var compactor = CreateCompactor("concurrent-summary");
        var compactTask = compactor.CompactAsync(session, new CompactionOptions
        {
            PreservedTurns = 2,
            SummarizationModel = TestModel.Id
        });
        var addTask = Task.Run(() => session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "late-user" }));

        Func<Task> act = async () => await Task.WhenAll(compactTask, addTask);
        await act.ShouldNotThrowAsync();

        // Apply compaction result if succeeded (caller responsibility after Bug 4 fix)
        var result = await compactTask;
        if (result.Succeeded && result.CompactedHistory is not null)
            session.ReplaceHistory(result.CompactedHistory);

        var history = session.GetHistorySnapshot();
        foreach (var entry in history)
        {
            entry.Role.Value.ShouldNotBeNullOrWhiteSpace();
            entry.Content.ShouldNotBeNull();
        }

        history.Count(entry => entry.IsCompactionSummary).ShouldBeLessThanOrEqualTo(1);
        // Phase 3a (#531): summary no longer sits at index 0; check it exists and is not marked historical.
        if (history.Any(entry => entry.IsCompactionSummary))
            history.Single(entry => entry.IsCompactionSummary).IsHistory.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CompactionEntry_JsonRoundTrip_PreservesFlag()
    {
        var entry = new SessionEntry
        {
            Role = MessageRole.System,
            Content = "summary",
            IsCompactionSummary = true
        };

        var serialized = JsonSerializer.Serialize(entry, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var roundTrip = JsonSerializer.Deserialize<SessionEntry>(serialized, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        roundTrip.ShouldNotBeNull();
        roundTrip!.IsCompactionSummary.ShouldBeTrue();
    }

    public void Dispose()
    {
        foreach (var path in _cleanupDirectories.Where(Directory.Exists))
            Directory.Delete(path, recursive: true);
    }

    private string CreateStorePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SessionCompactionIntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _cleanupDirectories.Add(path);
        return path;
    }

    private static GatewaySession BuildCompactionSession()
    {
        var session = new GatewaySession { SessionId = SessionId.From(Guid.NewGuid().ToString("N")), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "older-user" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "older-assistant" },
            new SessionEntry { Role = MessageRole.User, Content = "recent-user" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "recent-assistant" }
        ]);
        return session;
    }

    private static LlmSessionCompactor CreateCompactor(string summary)
    {
        var providers = new ApiProviderRegistry();
        var models = new ModelRegistry();
        models.Register(TestModel.Provider, TestModel);

        var provider = new Mock<IApiProvider>();
        provider.SetupGet(item => item.Api).Returns(TestModel.Api);
        provider.Setup(item => item.StreamSimple(
                It.IsAny<LlmModel>(),
                It.IsAny<Context>(),
                It.IsAny<SimpleStreamOptions?>()))
            .Returns(() => CreateStream(summary));

        providers.Register(provider.Object);
        var llmClient = new LlmClient(providers, models);
        return new LlmSessionCompactor(llmClient, NullLogger<LlmSessionCompactor>.Instance);
    }

    private static LlmStream CreateStream(string summary)
    {
        var stream = new LlmStream();
        var completion = new AssistantMessage(
            Content: [new TextContent(summary)],
            Api: TestModel.Api,
            Provider: TestModel.Provider,
            ModelId: TestModel.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new DoneEvent(StopReason.Stop, completion));
        stream.End(completion);
        return stream;
    }

    private sealed class MockFileStoreFixture : IDisposable
    {
        public MockFileStoreFixture()
        {
            FileSystem = new MockFileSystem();
            StorePath = Path.Combine(Path.GetTempPath(), "SessionCompactionIntegrationTests", Guid.NewGuid().ToString("N"));
            FileSystem.Directory.CreateDirectory(StorePath);
        }

        public MockFileSystem FileSystem { get; }
        public string StorePath { get; }

        public FileSessionStore CreateStore()
            => new(StorePath, NullLogger<FileSessionStore>.Instance, FileSystem);

        public void Dispose()
        {
            if (FileSystem.Directory.Exists(StorePath))
                FileSystem.Directory.Delete(StorePath, true);
        }
    }

    private sealed class SqliteStoreFixture : IDisposable
    {
        public SqliteStoreFixture()
        {
            DirectoryPath = Path.Combine(AppContext.BaseDirectory, "SessionCompactionSqliteTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "sessions.db");
            ConnectionString = $"Data Source={DatabasePath};Pooling=False";
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string ConnectionString { get; }
        public InMemoryConversationStore Conversations { get; } = new();

        public SqliteSessionStore CreateStore()
            => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, Conversations);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
