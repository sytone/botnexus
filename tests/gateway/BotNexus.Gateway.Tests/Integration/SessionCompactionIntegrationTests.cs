using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
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
        var session = await store.GetOrCreateAsync("compaction-persist", "agent-a");

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

        var reloaded = await fixture.CreateStore().GetAsync("compaction-persist");

        reloaded.ShouldNotBeNull();
        reloaded!.History.Count().ShouldBe(11);
        reloaded.History[0].IsCompactionSummary.ShouldBeTrue();
        reloaded.History[0].Content.ShouldBe("compaction-summary");
    }

    [Fact]
    public async Task MultipleCompactions_OnlyLastSummaryKept()
    {
        using var fixture = new MockFileStoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("multi-compaction", "agent-a");

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
        var reloaded = await fixture.CreateStore().GetAsync("multi-compaction");

        reloaded.ShouldNotBeNull();
        reloaded!.History.Select(entry => entry.Content)
            .ShouldBe(new[] { "summary-2", "tail-1", "tail-2" }, ignoreOrder: false);
        reloaded.History.Count().ShouldBe(3);
    }

    [Fact]
    public async Task CompactedSession_OriginalEntriesStillOnDisk()
    {
        var storePath = CreateStorePath();
        var sessionId = "raw-disk";
        var store = new FileSessionStore(storePath, NullLogger<FileSessionStore>.Instance, new FileSystem());
        var session = await store.GetOrCreateAsync(sessionId, "agent-a");

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
        var session = await store.GetOrCreateAsync("sqlite-compaction", "agent-a");

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
        var reloaded = await fixture.CreateStore().GetAsync("sqlite-compaction");

        reloaded.ShouldNotBeNull();
        reloaded!.History.Count().ShouldBe(11);
        reloaded.History[0].IsCompactionSummary.ShouldBeTrue();
        reloaded.History[0].Content.ShouldBe("sqlite-summary");
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
        await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 3,
            SummarizationModel = TestModel.Id
        });

        var history = session.GetHistorySnapshot();
        history.Count().ShouldBe(7);
        history[0].IsCompactionSummary.ShouldBeTrue();
        history.Skip(1).Select(entry => entry.Content)
            .ShouldBe(new[] { "user-8", "assistant-8", "user-9", "assistant-9", "user-10", "assistant-10" }, ignoreOrder: false);

        history.Skip(1).Select(entry => entry.Content)
            .ShouldBe(originalEntries.Skip(14).Select(entry => entry.Content));
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
        await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        session.GetHistorySnapshot().Select(entry => entry.Content)
            .ShouldBe(new[] { "tool-summary", "u2", "a2", "tool-call", "tool-result" }, ignoreOrder: false);
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
        await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "u3" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "a3" });

        session.GetHistorySnapshot().Select(entry => entry.Content)
            .ShouldBe(new[] { "coherent-summary", "u2", "a2", "u3", "a3" }, ignoreOrder: false);
    }

    [Fact]
    public async Task CompactThenArchive_BothOperationsWork()
    {
        var storePath = CreateStorePath();
        const string sessionId = "compact-archive";
        var encodedSessionId = Uri.EscapeDataString(sessionId);
        var store = new FileSessionStore(storePath, NullLogger<FileSessionStore>.Instance, new FileSystem());
        var session = await store.GetOrCreateAsync(sessionId, "agent-a");
        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "u1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a1" },
            new SessionEntry { Role = MessageRole.User, Content = "u2" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "a2" }
        ]);

        var compactor = CreateCompactor("archived-summary");
        await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });
        await store.SaveAsync(session);

        await store.ArchiveAsync(sessionId);
        var newSession = await store.GetOrCreateAsync(sessionId, "agent-a");

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

        await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            MaxSummaryChars = 16_000,
            SummarizationModel = TestModel.Id
        });

        session.GetHistorySnapshot().First().Content.Length.ShouldBeLessThanOrEqualTo(16_000);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CompactionSummary_EmptyResponse_HandledGracefully()
    {
        var session = BuildCompactionSession();
        var compactor = CreateCompactor(string.Empty);

        Func<Task> act = async () => await compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 1,
            SummarizationModel = TestModel.Id
        });

        await act.ShouldNotThrowAsync();
        session.GetHistorySnapshot().Count().ShouldBe(3);
        session.GetHistorySnapshot().Skip(1).Select(entry => entry.Content)
            .ShouldBe(new[] { "recent-user", "recent-assistant" }, ignoreOrder: false);
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
        var compactTask = compactor.CompactAsync(session.Session, new CompactionOptions
        {
            PreservedTurns = 2,
            SummarizationModel = TestModel.Id
        });
        var addTask = Task.Run(() => session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "late-user" }));

        Func<Task> act = async () => await Task.WhenAll(compactTask, addTask);
        await act.ShouldNotThrowAsync();

        var history = session.GetHistorySnapshot();
        foreach (var entry in history)
        {
            entry.Role.Value.ShouldNotBeNullOrWhiteSpace();
            entry.Content.ShouldNotBeNull();
        }

        history.Count(entry => entry.IsCompactionSummary).ShouldBeLessThanOrEqualTo(1);
        if (history.Any(entry => entry.IsCompactionSummary))
            history.First().IsCompactionSummary.ShouldBeTrue();
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
        var session = new GatewaySession { SessionId = Guid.NewGuid().ToString("N"), AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a") };
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

        public SqliteSessionStore CreateStore()
            => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
