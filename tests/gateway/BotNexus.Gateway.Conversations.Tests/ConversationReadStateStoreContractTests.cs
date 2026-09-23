using BotNexus.Domain.Primitives;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Conversations.Tests;

public abstract class ConversationReadStateStoreContractTests : IDisposable
{
    private static readonly ConversationReaderId Reader = ConversationReaderId.From("reader-1");
    private static readonly ConversationId Conversation = ConversationId.From("conversation-1");

    protected abstract IConversationReadStateStore CreateStore();

    protected virtual IConversationReadStateStore CreateConcurrentStore()
        => CreateStore();

    protected virtual IConversationReadStateStore ReopenStore()
        => throw new NotSupportedException();

    protected virtual bool SupportsReopen => false;

    public virtual void Dispose()
    {
    }

    [Fact]
    public async Task GetAsync_IsolatesByWorldReaderAndConversation()
    {
        var store = CreateStore();
        await store.AdvanceAsync("world-1", Reader, Conversation, ConversationReadPosition.From(10));

        (await store.GetAsync("world-2", Reader, Conversation)).ShouldBeNull();
        (await store.GetAsync("world-1", ConversationReaderId.From("reader-2"), Conversation)).ShouldBeNull();
        (await store.GetAsync("world-1", Reader, ConversationId.From("conversation-2"))).ShouldBeNull();
        (await store.GetAsync("world-1", Reader, Conversation)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AdvanceAsync_LowerDelayedPosition_DoesNotMoveBackward()
    {
        var store = CreateStore();
        await store.AdvanceAsync("world-1", Reader, Conversation, ConversationReadPosition.From(10));

        var state = await store.AdvanceAsync(
            "world-1", Reader, Conversation, ConversationReadPosition.From(8));

        state.Position.ShouldBe(ConversationReadPosition.From(10));
        state.Version.ShouldBe(1);
    }

    [Fact]
    public async Task AdvanceAsync_RetryIsIdempotentAndDoesNotBumpVersion()
    {
        var store = CreateStore();

        var first = await store.AdvanceAsync(
            "world-1", Reader, Conversation, ConversationReadPosition.From(10));
        var retry = await store.AdvanceAsync(
            "world-1", Reader, Conversation, ConversationReadPosition.From(10));
        var advanced = await store.AdvanceAsync(
            "world-1", Reader, Conversation, ConversationReadPosition.From(11));

        first.Version.ShouldBe(1);
        retry.ShouldBe(first);
        advanced.Position.ShouldBe(ConversationReadPosition.From(11));
        advanced.Version.ShouldBe(2);
    }

    [Fact]
    public async Task AdvanceAsync_RejectsUninitializedTypedKeys()
    {
        var store = CreateStore();
        var readers = new ConversationReaderId[1];
        var conversations = new ConversationId[1];

        await Should.ThrowAsync<ArgumentException>(() => store.AdvanceAsync(
            "world-1", readers[0], Conversation, ConversationReadPosition.From(1)));
        await Should.ThrowAsync<ArgumentException>(() => store.AdvanceAsync(
            "world-1", Reader, conversations[0], ConversationReadPosition.From(1)));
    }

    [Fact]
    public async Task AdvanceAsync_ConcurrentAndDelayedWrites_KeepHighestPosition()
    {
        var store = CreateStore();
        var concurrentStore = CreateConcurrentStore();
        var writes = Enumerable.Range(1, 64)
            .Reverse()
            .Select((position, index) => (Writer: (index & 1) == 0 ? store : concurrentStore, Position: position))
            .Select(item => item.Writer.AdvanceAsync(
                "world-1", Reader, Conversation, ConversationReadPosition.From(item.Position)));

        await Task.WhenAll(writes);

        var state = await store.GetAsync("world-1", Reader, Conversation);
        state.ShouldNotBeNull();
        state.Position.ShouldBe(ConversationReadPosition.From(64));
        state.Version.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task AdvanceAsync_ConcurrentRetries_CreateOneVersion()
    {
        var store = CreateStore();
        var writes = Enumerable.Range(0, 32)
            .Select(_ => store.AdvanceAsync(
                "world-1", Reader, Conversation, ConversationReadPosition.From(10)));

        await Task.WhenAll(writes);

        var state = await store.GetAsync("world-1", Reader, Conversation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_AfterReopen_ReturnsPersistedState()
    {
        if (!SupportsReopen)
            return;

        var store = CreateStore();
        await store.AdvanceAsync("world-1", Reader, Conversation, ConversationReadPosition.From(10));

        var reopened = ReopenStore();
        var state = await reopened.GetAsync("world-1", Reader, Conversation);

        state.ShouldNotBeNull();
        state.Position.ShouldBe(ConversationReadPosition.From(10));
        state.Version.ShouldBe(1);
    }
}

public sealed class InMemoryConversationReadStateStoreTests
    : ConversationReadStateStoreContractTests
{
    private readonly InMemoryConversationReadStateStore _store = new();

    protected override IConversationReadStateStore CreateStore()
        => _store;

    protected override IConversationReadStateStore CreateConcurrentStore()
        => _store;
}

public sealed class FileConversationReadStateStoreTests
    : ConversationReadStateStoreContractTests
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"botnexus-read-state-{Guid.NewGuid():N}");

    protected override bool SupportsReopen => true;

    protected override IConversationReadStateStore CreateStore()
        => new FileConversationReadStateStore(_directory);

    protected override IConversationReadStateStore CreateConcurrentStore()
        => new FileConversationReadStateStore(_directory);

    protected override IConversationReadStateStore ReopenStore()
        => new FileConversationReadStateStore(_directory);

    [Fact]
    public async Task AdvanceAsync_RepeatedKey_KeepsOneRecord()
    {
        var store = CreateStore();
        await store.AdvanceAsync(
            "world-1", ConversationReaderId.From("reader-1"), ConversationId.From("conversation-1"),
            ConversationReadPosition.From(10));
        await store.AdvanceAsync(
            "world-1", ConversationReaderId.From("reader-1"), ConversationId.From("conversation-1"),
            ConversationReadPosition.From(11));

        Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories).Length.ShouldBe(1);
    }

    public override void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}

public sealed class SqliteConversationReadStateStoreTests
    : ConversationReadStateStoreContractTests
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"botnexus-read-state-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_databasePath};Pooling=False";

    protected override bool SupportsReopen => true;

    protected override IConversationReadStateStore CreateStore()
        => new SqliteConversationReadStateStore(ConnectionString);

    protected override IConversationReadStateStore CreateConcurrentStore()
        => new SqliteConversationReadStateStore(ConnectionString);

    protected override IConversationReadStateStore ReopenStore()
        => new SqliteConversationReadStateStore(ConnectionString);

    [Fact]
    public async Task AdvanceAsync_RepeatedKey_KeepsOneRow()
    {
        var store = CreateStore();
        await store.AdvanceAsync(
            "world-1", ConversationReaderId.From("reader-1"), ConversationId.From("conversation-1"),
            ConversationReadPosition.From(10));
        await store.AdvanceAsync(
            "world-1", ConversationReaderId.From("reader-1"), ConversationId.From("conversation-1"),
            ConversationReadPosition.From(11));

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conversation_read_state";
        Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(1);
    }

    public override void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString);
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }
}
