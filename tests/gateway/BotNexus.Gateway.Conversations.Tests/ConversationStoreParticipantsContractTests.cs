using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// P9-F contract tests for <c>IConversationStore.AddParticipantsAsync</c> and the
/// participant-inclusion contract on <c>ListForCitizenAsync</c>. Runs against all three
/// store implementations (in-memory, file, SQLite) via a theory so each store gets
/// identical contract coverage.
/// </summary>
/// <remarks>
/// The 5 invariants verified per store:
/// <list type="number">
///   <item>Round-trip: <c>AddParticipantsAsync</c> followed by <c>GetAsync</c> returns the participants.</item>
///   <item>Idempotence: re-adding the same citizen is a no-op (no duplicates).</item>
///   <item>Multi-citizen merge: subsequent calls accumulate, they don't replace.</item>
///   <item>ListForCitizenAsync includes conversations where the citizen is a participant.</item>
///   <item>Concurrent <c>AddParticipantsAsync</c> from N tasks produces the union, not a race-condition subset.</item>
/// </list>
/// </remarks>
public sealed class ConversationStoreParticipantsContractTests
{
    public static IEnumerable<object[]> StoreHarnesses()
    {
        yield return ["in-memory", () => new InMemoryStoreHarness()];
        yield return ["file", () => new FileStoreHarness()];
        yield return ["sqlite", () => new SqliteStoreHarness()];
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task AddParticipantsAsync_RoundTripsThrough_GetAsync(string _, Func<IStoreHarness> create)
    {
        using var harness = create();
        var store = harness.Store;
        var convoId = ConversationId.Create();
        await store.CreateAsync(NewConversation(convoId, AgentId.From("owner")));

        var participant = new SessionParticipant
        {
            CitizenId = CitizenId.Of(AgentId.From("agent-alpha"))
        };
        await store.AddParticipantsAsync(convoId, [participant]);

        var loaded = await store.GetAsync(convoId);
        loaded.ShouldNotBeNull();
        loaded!.Participants.Count.ShouldBe(1);
        loaded.Participants[0].CitizenId.ShouldBe(CitizenId.Of(AgentId.From("agent-alpha")));
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task AddParticipantsAsync_IsIdempotent_ForSameCitizen(string _, Func<IStoreHarness> create)
    {
        using var harness = create();
        var store = harness.Store;
        var convoId = ConversationId.Create();
        await store.CreateAsync(NewConversation(convoId, AgentId.From("owner")));

        var participant = new SessionParticipant
        {
            CitizenId = CitizenId.Of(AgentId.From("agent-alpha"))
        };
        // P9-F: AddParticipantsAsync uses INSERT OR IGNORE / dedupe-by-citizen so the
        // second call must be a no-op rather than producing a duplicate row.
        await store.AddParticipantsAsync(convoId, [participant]);
        await store.AddParticipantsAsync(convoId, [participant]);

        var loaded = await store.GetAsync(convoId);
        loaded.ShouldNotBeNull();
        loaded!.Participants.Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task AddParticipantsAsync_AccumulatesAcrossCalls(string _, Func<IStoreHarness> create)
    {
        using var harness = create();
        var store = harness.Store;
        var convoId = ConversationId.Create();
        await store.CreateAsync(NewConversation(convoId, AgentId.From("owner")));

        await store.AddParticipantsAsync(convoId,
            [new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-alpha")) }]);
        await store.AddParticipantsAsync(convoId,
            [new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-beta")) }]);

        var loaded = await store.GetAsync(convoId);
        loaded.ShouldNotBeNull();
        loaded!.Participants.Count.ShouldBe(2);
        loaded.Participants.Select(p => p.CitizenId).ShouldContain(CitizenId.Of(AgentId.From("agent-alpha")));
        loaded.Participants.Select(p => p.CitizenId).ShouldContain(CitizenId.Of(AgentId.From("agent-beta")));
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ListForCitizenAsync_IncludesConversationsWhereCitizenIsParticipant(
        string _,
        Func<IStoreHarness> create)
    {
        using var harness = create();
        var store = harness.Store;

        // Conversation #1: agent-alpha owns it, agent-beta initiates — must show up for both.
        var ownedByAlpha = NewConversation(ConversationId.Create(), AgentId.From("agent-alpha"));
        await store.CreateAsync(ownedByAlpha);

        // Conversation #2: agent-gamma owns it, agent-beta is added as a participant.
        // Pre-P9-F, ListForCitizenAsync(beta) would NOT have included this; the new
        // contract says it MUST.
        var ownedByGamma = NewConversation(ConversationId.Create(), AgentId.From("agent-gamma"));
        await store.CreateAsync(ownedByGamma);
        await store.AddParticipantsAsync(
            ownedByGamma.ConversationId,
            [new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-beta")) }]);

        // Conversation #3: unrelated — must NOT show up for beta.
        var unrelated = NewConversation(ConversationId.Create(), AgentId.From("agent-delta"));
        await store.CreateAsync(unrelated);

        var betaConversations = await store.ListForCitizenAsync(CitizenId.Of(AgentId.From("agent-beta")));
        var ids = betaConversations.Select(c => c.ConversationId).ToList();
        ids.ShouldContain(ownedByGamma.ConversationId,
            "P9-F: ListForCitizenAsync must include conversations where the citizen is a participant.");
        ids.ShouldNotContain(unrelated.ConversationId,
            "ListForCitizenAsync must NOT include unrelated conversations.");
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task AddParticipantsAsync_ConcurrentCalls_ProduceUnion_NotRaceSubset(
        string _,
        Func<IStoreHarness> create)
    {
        using var harness = create();
        var store = harness.Store;
        var convoId = ConversationId.Create();
        await store.CreateAsync(NewConversation(convoId, AgentId.From("owner")));

        // P9-F: AddParticipantsAsync MUST be atomic. The pre-P9-F shape was
        // read-modify-write through SaveAsync which could clobber concurrent additions.
        // This test fans out N parallel additions of distinct citizens and asserts the
        // final state is the union.
        const int N = 16;
        var tasks = Enumerable.Range(0, N).Select(i =>
            store.AddParticipantsAsync(
                convoId,
                [new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From($"agent-{i:D2}")) }])
        ).ToArray();
        await Task.WhenAll(tasks);

        var loaded = await store.GetAsync(convoId);
        loaded.ShouldNotBeNull();
        loaded!.Participants.Count.ShouldBe(N,
            "P9-F atomic-merge contract: concurrent AddParticipantsAsync calls must produce " +
            "the union, not a race-condition subset (which would indicate a read-modify-write).");
    }

    private static Conversation NewConversation(ConversationId id, AgentId agentId)
        => new()
        {
            ConversationId = id,
            AgentId = agentId,
            Title = "test-conv",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    public interface IStoreHarness : IDisposable
    {
        IConversationStore Store { get; }
    }

    private sealed class InMemoryStoreHarness : IStoreHarness
    {
        public IConversationStore Store { get; } = new InMemoryConversationStore();
        public void Dispose() { }
    }

    private sealed class FileStoreHarness : IStoreHarness
    {
        private readonly MockFileSystem _fileSystem = new();
        private readonly string _rootPath =
            Path.Combine(Path.GetTempPath(), "ConversationStoreParticipantsContractTests", Guid.NewGuid().ToString("N"));

        public FileStoreHarness()
        {
            _fileSystem.Directory.CreateDirectory(_rootPath);
            Store = new FileConversationStore(_rootPath, NullLogger<FileConversationStore>.Instance, _fileSystem);
        }

        public IConversationStore Store { get; }

        public void Dispose()
        {
            if (_fileSystem.Directory.Exists(_rootPath))
                _fileSystem.Directory.Delete(_rootPath, true);
        }
    }

    private sealed class SqliteStoreHarness : IStoreHarness
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bn-p9f-conv-{Guid.NewGuid():N}.db");

        public SqliteStoreHarness()
        {
            Store = new SqliteConversationStore(
                $"Data Source={_dbPath};Pooling=False",
                NullLogger<SqliteConversationStore>.Instance);
        }

        public IConversationStore Store { get; }

        public void Dispose()
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }
}
