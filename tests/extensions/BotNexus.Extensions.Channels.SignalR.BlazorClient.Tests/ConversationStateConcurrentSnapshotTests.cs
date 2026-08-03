using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Shouldly;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Issue #2712: the conversation timeline was an unsynchronised <c>List&lt;ChatMessage&gt;</c> exposed as a
/// LIVE view. Callers snapshotted it with <c>.ToArray()</c> (the #2320 fix), but <c>List.ToArray()</c> is
/// not atomic -- it reads <c>Count</c>, allocates an array of that size, then <c>CopyTo</c>s. A concurrent
/// <c>Add</c> landing between the length read and the copy overran the destination and threw
/// <c>ArgumentException: Destination array was not long enough</c>.
///
/// These tests pin the guarantee at the STORE layer, independent of the bUnit render harness: a reader
/// hammering the snapshot path while a writer appends must never observe a torn state, and the id-&gt;index
/// map from #1622 must stay consistent with the timeline it describes under the same guard.
/// </summary>
public sealed class ConversationStateConcurrentSnapshotTests
{
    private const int Appends = 20_000;

    [Fact]
    public async Task Snapshotting_messages_under_concurrent_append_never_throws()
    {
        var conv = new ConversationState { ConversationId = "c-1" };
        var failures = new List<Exception>();

        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < Appends; i++)
                conv.AppendMessage(new ChatMessage("assistant", $"m-{i}", DateTimeOffset.UtcNow) { Id = $"id-{i}" });
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // The exact shape of the call sites in Chat.razor (#2320): take the read-only view
                    // off the store, then copy it. Under the defect this raced the writer's Add.
                    var snapshot = conv.Messages.ToArray();

                    // A torn snapshot also shows up as a null slot where CopyTo never wrote: assert the
                    // copy is fully populated, not merely that it did not throw.
                    foreach (var message in snapshot)
                        message.ShouldNotBeNull();
                }
                catch (Exception ex)
                {
                    lock (failures)
                        failures.Add(ex);
                    return;
                }
            }
        })).ToArray();

        await writer;
        stop.Cancel();
        await Task.WhenAll(readers);

        failures.ShouldBeEmpty();
        conv.Messages.Count.ShouldBe(Appends);
    }

    [Fact]
    public async Task Message_index_stays_consistent_with_the_timeline_under_concurrent_append()
    {
        var conv = new ConversationState { ConversationId = "c-1" };
        var failures = new List<Exception>();

        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < Appends; i++)
                conv.AppendMessage(new ChatMessage("assistant", $"m-{i}", DateTimeOffset.UtcNow) { Id = $"id-{i}" });
        });

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // #1622's invariant, read concurrently: every mapped index must be in range for the
                    // timeline snapshot and must point at the message carrying that id.
                    var messages = conv.Messages;
                    var index = conv.MessageIndex;

                    foreach (var pair in index)
                    {
                        if (pair.Value >= messages.Count)
                            continue; // map observed ahead of an older timeline snapshot: not a tear.

                        messages[pair.Value].Id.ShouldBe(pair.Key);
                    }
                }
                catch (Exception ex)
                {
                    lock (failures)
                        failures.Add(ex);
                    return;
                }
            }
        });

        await writer;
        stop.Cancel();
        await reader;

        failures.ShouldBeEmpty();

        // And after the storm the map describes the final timeline exactly.
        var final = conv.Messages;
        final.Count.ShouldBe(Appends);
        var finalIndex = conv.MessageIndex;
        finalIndex.Count.ShouldBe(Appends);
        for (var i = 0; i < final.Count; i++)
        {
            finalIndex.TryGetValue(final[i].Id!, out var mapped).ShouldBeTrue($"id {final[i].Id} missing from MessageIndex");
            mapped.ShouldBe(i);
        }
    }

    [Fact]
    public async Task Concurrent_appends_from_multiple_writers_lose_no_messages()
    {
        var conv = new ConversationState { ConversationId = "c-1" };
        const int perWriter = 2_000;
        const int writers = 4;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
                conv.AppendMessage(new ChatMessage("assistant", $"w{w}-{i}", DateTimeOffset.UtcNow) { Id = $"id-{w}-{i}" });
        })));

        conv.Messages.Count.ShouldBe(perWriter * writers);
        conv.MessageIndex.Count.ShouldBe(perWriter * writers);
    }

    [Fact]
    public void Messages_snapshot_is_stable_across_a_later_append()
    {
        var conv = new ConversationState { ConversationId = "c-1" };
        conv.AppendMessage(new ChatMessage("assistant", "first", DateTimeOffset.UtcNow) { Id = "id-1" });

        var snapshot = conv.Messages;
        snapshot.Count.ShouldBe(1);

        conv.AppendMessage(new ChatMessage("assistant", "second", DateTimeOffset.UtcNow) { Id = "id-2" });

        // The previously handed-out view must not have mutated underneath its holder -- that immutability
        // is what makes an in-flight render pass safe (#2712).
        snapshot.Count.ShouldBe(1);
        conv.Messages.Count.ShouldBe(2);
    }
}
