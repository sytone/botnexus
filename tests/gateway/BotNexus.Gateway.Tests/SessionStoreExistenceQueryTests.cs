using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class SessionStoreExistenceQueryTests
{
    public static IEnumerable<object[]> StoreHarnesses()
    {
        yield return ["in-memory", () => new InMemoryHarness()];
        yield return ["file", () => new FileHarness()];
        yield return ["sqlite", () => new SqliteHarness()];
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task GetExistenceAsync_ReturnsSessionsOwnedByAgent(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedExistenceDataAsync(harness);

        var sessions = await harness.Store.GetExistenceAsync(AgentId.From("agent-a"), new ExistenceQuery());

        var ids0 = sessions.Select(s => s.SessionId.Value);
        ids0.ShouldContain("owned");
        ids0.ShouldContain("both");
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task GetExistenceAsync_ReturnsSessionsWhereAgentParticipates(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedExistenceDataAsync(harness);

        var sessions = await harness.Store.GetExistenceAsync(AgentId.From("agent-a"), new ExistenceQuery());

        var ids1 = sessions.Select(s => s.SessionId.Value);
        ids1.ShouldContain("participant");
        ids1.ShouldContain("both");
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task GetExistenceAsync_ReturnsUnionWithoutDuplicates(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedExistenceDataAsync(harness);

        var sessions = await harness.Store.GetExistenceAsync(AgentId.From("agent-a"), new ExistenceQuery());
        var ids = sessions.Select(s => s.SessionId.Value).ToList();

        ids.ShouldContain("owned");
        ids.ShouldContain("participant");
        ids.ShouldContain("both");
        ids.ShouldBeUnique();
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task GetExistenceAsync_WithDateRangeFilter_Works(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        var now = await SeedExistenceDataAsync(harness);

        var sessions = await harness.Store.GetExistenceAsync(
            AgentId.From("agent-a"),
            new ExistenceQuery
            {
                From = now.AddDays(-2.5),
                To = now.AddDays(-1.5)
            });

        sessions.Select(s => s.SessionId.Value).ShouldHaveSingleItem().ShouldBe("participant");
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task GetExistenceAsync_WithSessionTypeFilter_Works(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedExistenceDataAsync(harness);

        var sessions = await harness.Store.GetExistenceAsync(
            AgentId.From("agent-a"),
            new ExistenceQuery
            {
                // P9-E (#645): SessionType.Cron deleted; the seeded "participant" row
                // (line ~180) uses AgentAgent so the TypeFilter still isolates it
                // (distinct from "owned"/UserAgent and "both"/AgentSubAgent).
                TypeFilter = SessionType.AgentAgent
            });

        sessions.Select(s => s.SessionId.Value).ShouldHaveSingleItem().ShouldBe("participant");
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task GetExistenceAsync_WithLimit_Works(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedExistenceDataAsync(harness);

        var sessions = await harness.Store.GetExistenceAsync(
            AgentId.From("agent-a"),
            new ExistenceQuery
            {
                Limit = 2
            });

        sessions.Count().ShouldBeLessThanOrEqualTo(2);
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task GetExistenceAsync_ReturnsEmptyForUnknownAgent(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedExistenceDataAsync(harness);

        var sessions = await harness.Store.GetExistenceAsync(AgentId.From("agent-unknown"), new ExistenceQuery());

        sessions.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task GetExistenceAsync_WithNullQuery_ReturnsAllExistence(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await SeedExistenceDataAsync(harness);

        var sessions = await harness.Store.GetExistenceAsync(AgentId.From("agent-a"), null!);

        var allIds = sessions.Select(s => s.SessionId.Value);
        allIds.ShouldContain("owned");
        allIds.ShouldContain("participant");
        allIds.ShouldContain("both");
    }

    private static async Task<DateTimeOffset> SeedExistenceDataAsync(IStoreHarness harness)
    {
        var store = harness.Store;
        var conversations = harness.Conversations;
        var now = DateTimeOffset.UtcNow;

        // P9-F: Participants live on Conversation now. Seed two participant conversations
        // for agent-a, one owned by agent-b ("participant" session) and one owned by
        // agent-a itself ("both" session).
        var convParticipant = ConversationId.From("conv-participant");
        var convBoth = ConversationId.From("conv-both");
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = convParticipant,
            AgentId = AgentId.From("agent-b"),
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2)
        });
        await conversations.AddParticipantsAsync(
            convParticipant,
            [new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-a")) }]);
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = convBoth,
            AgentId = AgentId.From("agent-a"),
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        });
        await conversations.AddParticipantsAsync(
            convBoth,
            [new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-a")) }]);

        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("owned"),
            AgentId = AgentId.From("agent-a"),
            SessionType = SessionType.UserAgent,
            CreatedAt = now.AddDays(-3),
            UpdatedAt = now.AddDays(-3)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("participant"),
            AgentId = AgentId.From("agent-b"),
            ConversationId = convParticipant,
            // P9-E (#645): SessionType.Cron deleted. Use AgentAgent here — it's distinct
            // from "owned"/UserAgent and "both"/AgentSubAgent so the TypeFilter test
            // (which selects participant via SessionType) still isolates this row.
            SessionType = SessionType.AgentAgent,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("both"),
            AgentId = AgentId.From("agent-a"),
            ConversationId = convBoth,
            SessionType = SessionType.AgentSubAgent,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("other"),
            AgentId = AgentId.From("agent-z"),
            SessionType = SessionType.UserAgent,
            CreatedAt = now,
            UpdatedAt = now
        });
        return now;
    }

    public interface IStoreHarness : IDisposable
    {
        ISessionStore Store { get; }
        IConversationStore Conversations { get; }
    }

    private sealed class InMemoryHarness : IStoreHarness
    {
        public IConversationStore Conversations { get; } = new InMemoryConversationStore();
        public ISessionStore Store { get; }

        public InMemoryHarness()
        {
            Store = new InMemorySessionStore(redactor: null, conversationStore: Conversations);
        }

        public void Dispose() { }
    }

    private sealed class FileHarness : IStoreHarness
    {
        private readonly MockFileSystem _fileSystem = new();
        private readonly string _storePath = Path.Combine(Path.GetTempPath(), "SessionStoreExistenceQueryTests", Guid.NewGuid().ToString("N"));

        public FileHarness()
        {
            _fileSystem.Directory.CreateDirectory(_storePath);
            Conversations = new InMemoryConversationStore();
            Store = new FileSessionStore(_storePath, NullLogger<FileSessionStore>.Instance, _fileSystem, Conversations);
        }

        public IConversationStore Conversations { get; }
        public ISessionStore Store { get; }

        public void Dispose()
        {
            if (_fileSystem.Directory.Exists(_storePath))
                _fileSystem.Directory.Delete(_storePath, true);
        }
    }

    private sealed class SqliteHarness : IStoreHarness
    {
        private readonly string _directoryPath;

        public SqliteHarness()
        {
            _directoryPath = Path.Combine(AppContext.BaseDirectory, "SessionStoreExistenceQueryTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directoryPath);
            var dbPath = Path.Combine(_directoryPath, "sessions.db");
            Conversations = new InMemoryConversationStore();
            Store = new SqliteSessionStore($"Data Source={dbPath};Pooling=False", NullLogger<SqliteSessionStore>.Instance, Conversations);
        }

        public IConversationStore Conversations { get; }
        public ISessionStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
