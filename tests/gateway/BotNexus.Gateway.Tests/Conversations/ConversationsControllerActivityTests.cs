using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using BotNexus.Gateway.Abstractions.Sessions;
using NSubstitute;
namespace BotNexus.Gateway.Tests.Conversations;
public sealed class ConversationsControllerActivityTests
{
    [Fact]
    public async Task Activity_bound_any_bound_preserves_binding_activity_and_legacy_timestamp()
    {
        var store = new InMemoryConversationStore();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var old = cutoff.AddDays(-30);
        var latest = cutoff.AddHours(12);
        var conversation = new Conversation
        {
            ConversationId = ConversationId.From("traffic"), AgentId = AgentId.From("alpha"),
            Title = "traffic", CreatedAt = old, UpdatedAt = old
        };
        conversation.ChannelBindings.Add(new ChannelBinding
        {
            BindingId = BindingId.Create(), ChannelType = ChannelKey.From("signalr"),
            ChannelAddress = ChannelAddress.From("test"), LastInboundAt = latest
        });
        await store.CreateAsync(conversation);
        var controller = new ConversationsController(store, new InMemorySessionStore());
        foreach (var since in new DateTimeOffset?[] { cutoff, null, cutoff })
        {
            var result = (await controller.List(null, CancellationToken.None, since, true))
                .ShouldBeOfType<OkObjectResult>();
            var rows = result.Value as IReadOnlyList<ConversationSummary>
                ?? throw new InvalidOperationException("Missing summaries");
            rows.ShouldHaveSingleItem().UpdatedAt.ShouldBe(latest);
        }
        var legacy = (await controller.List(null, CancellationToken.None)).ShouldBeOfType<OkObjectResult>();
        legacy.Value.ShouldBeAssignableTo<IReadOnlyList<ConversationSummary>>()
            .ShouldHaveSingleItem().UpdatedAt.ShouldBe(old);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("inbound")]
    [InlineData("outbound")]
    public async Task Agent_activity_uses_effective_timestamp_and_keeps_citizen_scope(string signal)
    {
        var store = new InMemoryConversationStore();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        var old = cutoff.AddDays(-30);
        var latest = cutoff;
        foreach (var (id, owner) in new[] { ("owned", "alpha"), ("other", "beta"), ("participant", "beta"), ("old", "alpha") })
        {
            var conversation = new Conversation
            {
                ConversationId = ConversationId.From(id), AgentId = AgentId.From(owner), Title = id,
                CreatedAt = signal == "created" && id != "old" ? latest : old,
                UpdatedAt = signal == "updated" && id != "old" ? latest : old
            };
            if (id == "participant")
                conversation.Participants.Add(new SessionParticipant
                {
                    CitizenId = CitizenId.Of(AgentId.From("alpha")), Role = "peer"
                });
            conversation.ChannelBindings.Add(new ChannelBinding
            {
                BindingId = BindingId.Create(), ChannelType = ChannelKey.From("signalr"),
                ChannelAddress = ChannelAddress.From(id),
                LastInboundAt = signal == "inbound" && id != "old" ? latest : null,
                LastOutboundAt = signal == "outbound" && id != "old" ? latest : null
            });
            await store.CreateAsync(conversation);
        }
        var controller = new ConversationsController(store, new InMemorySessionStore());
        foreach (var (since, effective) in new (DateTimeOffset?, bool)[]
                 { (cutoff, false), (null, true), (cutoff, true) })
        {
            var result = (await controller.List("alpha", CancellationToken.None, since, effective))
                .ShouldBeOfType<OkObjectResult>();
            var rows = result.Value as IReadOnlyList<ConversationSummary>
                ?? throw new InvalidOperationException("Missing summaries");
            rows.Where(row => row.ConversationId != "old").Select(row => row.ConversationId)
                .ShouldBe(new[] { "owned", "participant" });
            rows.Where(row => row.ConversationId != "old").ShouldAllBe(row => row.UpdatedAt == latest);
            rows.Any(row => row.ConversationId == "old").ShouldBe(since is null);
        }
    }

    [Fact]
    public async Task List_passes_cutoff_to_store_without_session_enrichment_or_unbounded_read()
    {
        var store = Substitute.For<IConversationStore>();
        var sessions = Substitute.For<ISessionStore>();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        store.GetSummariesAsync(cutoff, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConversationSummary>>([]));
        var controller = new ConversationsController(store, sessions);
        (await controller.List(null, CancellationToken.None, cutoff)).ShouldBeOfType<OkObjectResult>();
        await store.Received(1).GetSummariesAsync(cutoff, Arg.Any<CancellationToken>());
        await store.DidNotReceive().GetSummariesAsync(Arg.Any<CancellationToken>());
        sessions.ReceivedCalls().ShouldBeEmpty();
    }
    [Fact]
    public async Task List_threads_absolute_inclusive_cutoff_without_changing_default_query()
    {
        var store = new InMemoryConversationStore();
        var cutoff = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        foreach (var (id, at) in new[] { ("old", cutoff.AddTicks(-1)), ("boundary", cutoff) })
            await store.CreateAsync(new Conversation
            {
                ConversationId = ConversationId.From(id), AgentId = AgentId.From("alpha"), Title = id,
                CreatedAt = cutoff.AddDays(-2), UpdatedAt = at
            });
        var controller = new ConversationsController(store, new InMemorySessionStore());
        var bounded = (await controller.List(null, CancellationToken.None, cutoff)).ShouldBeOfType<OkObjectResult>();
        var boundedRows = bounded.Value as IReadOnlyList<ConversationSummary>
            ?? throw new InvalidOperationException("Missing summaries");
        boundedRows.Select(s => s.ConversationId).ShouldBe(new[] { "boundary" });
        var unbounded = (await controller.List(null, CancellationToken.None)).ShouldBeOfType<OkObjectResult>();
        var unboundedRows = unbounded.Value as IReadOnlyList<ConversationSummary>
            ?? throw new InvalidOperationException("Missing summaries");
        unboundedRows.Count.ShouldBe(2);
    }
}
