using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Concurrency;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Conversations.Tests;

public sealed class SqliteConversationStoreBindingStripeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveBindingAsync_DistinctIdsOnSameStripe_CompletesAndPreservesBindings(bool missing)
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var (from, to) = FindCollidingIds();
        await store.CreateAsync(new Conversation { ConversationId = from, AgentId = AgentId.From("agent-a"), Title = "source" });
        await store.CreateAsync(new Conversation { ConversationId = to, AgentId = AgentId.From("agent-a"), Title = "target" });
        var binding = new ChannelBinding
        {
            BindingId = BindingId.From("binding-stripe"),
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("address-stripe")
        };
        if (!missing)
            (await store.AddBindingAsync(from, binding)).ShouldBeTrue();

        // Cancellation bounds the known self-deadlock without leaking a blocked writer.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var moved = await store.MoveBindingAsync(from, to, binding.BindingId, deadline.Token);
        moved.ShouldBe(!missing);
        var source = await store.GetAsync(from);
        var target = await store.GetAsync(to);
        source.ShouldNotBeNull();
        target.ShouldNotBeNull();
        source.ChannelBindings.ShouldBeEmpty();
        if (missing)
            target.ChannelBindings.ShouldBeEmpty();
        else
            target.ChannelBindings.ShouldHaveSingleItem().BindingId.ShouldBe(binding.BindingId);
        // Both stripes must be released after success and after the missing-binding return.
        await store.PinAsync(from, true, deadline.Token);
        await store.PinAsync(to, true, deadline.Token);
    }

    private static (ConversationId From, ConversationId To) FindCollidingIds()
    {
        var seen = new Dictionary<int, ConversationId>();
        // Pigeonhole guarantee, independent of per-process randomized string hashing.
        for (var i = 0; i <= StripedAsyncLock.DefaultStripeCount; i++)
        {
            var id = ConversationId.From($"c_stripe_{i}");
            var hash = id.Value.GetHashCode();
            var stripe = (hash == int.MinValue ? 0 : Math.Abs(hash)) % StripedAsyncLock.DefaultStripeCount;
            if (seen.TryGetValue(stripe, out var first))
                return (first, id);
            seen.Add(stripe, id);
        }
        throw new InvalidOperationException("More keys than stripes must contain a collision.");
    }
}
