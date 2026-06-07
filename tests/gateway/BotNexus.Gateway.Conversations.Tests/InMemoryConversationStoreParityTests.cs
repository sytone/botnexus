using BotNexus.Gateway.Abstractions.Conversations;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Runs the <see cref="ConversationStoreContractTests"/> suite against <see cref="InMemoryConversationStore"/>.
/// </summary>
public sealed class InMemoryConversationStoreParityTests : ConversationStoreContractTests
{
    protected override IConversationStore CreateStore() => new InMemoryConversationStore();
}
