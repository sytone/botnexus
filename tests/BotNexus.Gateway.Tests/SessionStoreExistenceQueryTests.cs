using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
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
        await SeedExistenceDataAsync(harness.Store);

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
        await SeedExistenceDataAsync(harness.Store);

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
        await SeedExistenceDataAsync(harness.Store);

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
        var now = await SeedExistenceDataAsync(harness.Store);

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
        await SeedExistenceDataAsync(harness.Store);

        var sessions = await harness.Store.GetExistenceAsync(
            AgentId.From("agent-a"),
            new ExistenceQuery
            {
                TypeFilter = SessionType.Cron
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
        await SeedExistenceDataAsync(harness.Store);

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
        await SeedExistenceDataAsync(harness.Store);

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
        await SeedExistenceDataAsync(harness.Store);

        var sessions = await harness.Store.GetExistenceAsync(AgentId.From("agent-a"), null!);

        var allIds = sessions.Select(s => s.SessionId.Value);
        allIds.ShouldContain("owned");
        allIds.ShouldContain("participant");
        allIds.ShouldContain("both");
    }

    private static async Task<DateTimeOffset> SeedExistenceDataAsync(ISessionStore store)
    {
        var now = DateTimeOffset.UtcNow;
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
            SessionType = SessionType.Cron,
            Participants =
            [
                new SessionParticipant
                {
                    Type = ParticipantType.Agent,
                    Id = "agent-a"
                }
            ],
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2)
        });
        await store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("both"),
            AgentId = AgentId.From("agent-a"),
            SessionType = SessionType.AgentSubAgent,
            Participants =
            [
                new SessionParticipant
                {
                    Type = ParticipantType.Agent,
                    Id = "agent-a"
                }
            ],
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
    }

    private sealed class InMemoryHarness : IStoreHarness
    {
        public ISessionStore Store { get; } = new InMemorySessionStore();
        public void Dispose() { }
    }

    private sealed class FileHarness : IStoreHarness
    {
        private readonly MockFileSystem _fileSystem = new();
        private readonly string _storePath = Path.Combine(Path.GetTempPath(), "SessionStoreExistenceQueryTests", Guid.NewGuid().ToString("N"));

        public FileHarness()
        {
            _fileSystem.Directory.CreateDirectory(_storePath);
            Store = new FileSessionStore(_storePath, NullLogger<FileSessionStore>.Instance, _fileSystem);
        }

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
            Store = new SqliteSessionStore($"Data Source={dbPath};Pooling=False", NullLogger<SqliteSessionStore>.Instance);
        }

        public ISessionStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
