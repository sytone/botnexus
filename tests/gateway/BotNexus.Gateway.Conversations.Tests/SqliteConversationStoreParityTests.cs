using BotNexus.Gateway.Abstractions.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Runs the <see cref="ConversationStoreContractTests"/> suite against <see cref="SqliteConversationStore"/>.
/// Uses a temporary SQLite database file per test class execution.
/// </summary>
public sealed class SqliteConversationStoreParityTests : ConversationStoreContractTests, IDisposable
{
    private readonly StoreFixture _fixture = new();

    protected override IConversationStore CreateStore() => _fixture.CreateStore();

    // Exercise list materialisation under real LRU eviction pressure (#2226): the Sqlite store
    // has a bounded read-through cache, so cap it below the capacity-stress dataset size.
    protected override IConversationStore CreateCapacityConstrainedStore(int capacity)
        => _fixture.CreateStore(cacheCapacity: capacity);

    protected override void DisposeStore() => _fixture.Dispose();

    public void Dispose() => _fixture.Dispose();
}
