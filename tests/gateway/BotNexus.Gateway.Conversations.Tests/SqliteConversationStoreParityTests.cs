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

    protected override void DisposeStore() => _fixture.Dispose();

    public void Dispose() => _fixture.Dispose();
}
