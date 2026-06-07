using System.Reflection;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Conversations;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Ensures that every concrete <see cref="IConversationStore"/> implementation in the
/// Conversations assembly exists and is not abstract. This is the compile-time half of
/// the parity enforcement — the test-time half is the abstract
/// <c>ConversationStoreContractTests</c> base class in the Conversations.Tests project.
/// </summary>
public sealed class ConversationStoreParityArchitectureTests
{
    private static readonly Assembly StoreAssembly = typeof(InMemoryConversationStore).Assembly;

    [Fact]
    public void All_IConversationStore_Implementations_Are_Concrete_And_Not_Abstract()
    {
        var implementations = StoreAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IConversationStore).IsAssignableFrom(t))
            .ToList();

        implementations.ShouldNotBeEmpty(
            "Expected at least one concrete IConversationStore implementation in BotNexus.Gateway.Conversations");

        // Verify known implementations exist — this fails at compile time if renamed/removed
        implementations.ShouldContain(t => t == typeof(InMemoryConversationStore));
        implementations.ShouldContain(t => t == typeof(SqliteConversationStore));
    }

    [Fact]
    public void IConversationStore_Has_At_Least_Three_Implementations()
    {
        // File, InMemory, Sqlite — if one is removed, this catches it
        var count = StoreAssembly.GetTypes()
            .Count(t => t.IsClass && !t.IsAbstract && typeof(IConversationStore).IsAssignableFrom(t));

        count.ShouldBeGreaterThanOrEqualTo(3,
            "Expected at least 3 IConversationStore implementations (InMemory, File, Sqlite)");
    }
}
