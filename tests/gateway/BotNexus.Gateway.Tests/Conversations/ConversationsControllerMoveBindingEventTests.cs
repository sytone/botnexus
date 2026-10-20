using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>Pins the producer-only, post-commit binding-move facts and compatibility boundary.</summary>
public sealed class ConversationsControllerMoveBindingEventTests
{
    private static readonly AgentId Agent = AgentId.From("move-events-2088");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveBinding_PublishesBothCommittedDetachedFacts_AndPreservesLegacy(bool sqlite)
    {
        using var fixture = new MoveFixture(sqlite);
        await fixture.SeedAsync();
        var sink = new CommittedSink(fixture);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new RecordingNotifier();
        var controller = Controller(fixture.Store, publisher, notifier);
        var original = ConversationBindingSnapshot.From(fixture.Moved);

        var result = await controller.MoveBinding(fixture.Source.Value, fixture.Moved.BindingId.Value,
            new MoveBindingRequest(fixture.Target.Value), CancellationToken.None);

        var response = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<BindingResponse>();
        response.BindingId.ShouldBe(fixture.Moved.BindingId.Value);
        await publisher.WaitForDrainAsync();
        sink.Events.Count.ShouldBe(2);
        sink.CommittedChecks.ShouldBe(2);
        var removed = sink.Events.OfType<ConversationBindingRemovedEvent>().ShouldHaveSingleItem();
        var added = sink.Events.OfType<ConversationBindingAddedEvent>().ShouldHaveSingleItem();
        removed.ConversationId.ShouldBe(fixture.Source);
        added.ConversationId.ShouldBe(fixture.Target);
        removed.Binding.ShouldBe(original);
        added.Binding.ShouldBe(original);
        foreach (var fact in sink.Events)
        {
            fact.AgentId.ShouldBe(Agent);
            fact.SessionId.ShouldBeNull();
            var persisted = (await fixture.ReadFreshAsync(fact.ConversationId)).ShouldNotBeNull();
            fact.OccurredAt.ShouldBe(persisted.UpdatedAt);
            fact.Bindings.ShouldBe(ConversationBindingSnapshot.FromMany(persisted.ChannelBindings));
            foreach (var binding in persisted.ChannelBindings)
                binding.ChannelAddress = ChannelAddress.From("changed-after-publication");
            persisted.ChannelBindings.Clear();
        }
        fixture.Moved.ChannelAddress = ChannelAddress.From("changed-original");
        removed.Binding.ShouldBe(original);
        added.Binding.ShouldBe(original);
        removed.Bindings.ShouldHaveSingleItem().ChannelAddress.ShouldBe(ChannelAddress.From("source-retained"));
        added.Bindings.Select(b => b.ChannelAddress.Value).Order().ShouldBe(new[] { "moved", "target-retained" });
        AssertLegacy(notifier, fixture);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MoveBinding_FailedWrite_EmitsNeitherSuccessPath(bool sqlite, bool throws)
    {
        using var fixture = new MoveFixture(sqlite);
        await fixture.SeedAsync();
        var boundary = ReadThrough(fixture.Store);
        var setup = boundary.Setup(s => s.MoveBindingAsync(fixture.Source, fixture.Target, fixture.Moved.BindingId,
            It.IsAny<CancellationToken>()));
        if (throws) setup.ThrowsAsync(new InvalidOperationException("write failed"));
        else setup.ReturnsAsync(false);
        var sink = new CommittedSink(fixture);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new RecordingNotifier();
        var controller = Controller(boundary.Object, publisher, notifier);
        if (throws)
            await Should.ThrowAsync<InvalidOperationException>(() => controller.MoveBinding(fixture.Source.Value,
                fixture.Moved.BindingId.Value, new MoveBindingRequest(fixture.Target.Value), CancellationToken.None));
        else
            (await controller.MoveBinding(fixture.Source.Value, fixture.Moved.BindingId.Value,
                new MoveBindingRequest(fixture.Target.Value), CancellationToken.None)).ShouldBeOfType<NotFoundObjectResult>();
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        notifier.Calls.ShouldBeEmpty();
        (await fixture.ReadFreshAsync(fixture.Source)).ShouldNotBeNull().ChannelBindings
            .ShouldContain(b => b.BindingId == fixture.Moved.BindingId);
        (await fixture.ReadFreshAsync(fixture.Target)).ShouldNotBeNull().ChannelBindings
            .ShouldNotContain(b => b.BindingId == fixture.Moved.BindingId);
    }

    [Theory]
    [InlineData("blank", 400)]
    [InlineData("same", 400)]
    [InlineData("missing-source", 404)]
    [InlineData("missing-target", 404)]
    [InlineData("missing-binding", 404)]
    [InlineData("cross-agent", 400)]
    [InlineData("collision", 409)]
    [InlineData("source-readonly", 403)]
    [InlineData("target-readonly", 403)]
    [InlineData("source-hidden", 404)]
    [InlineData("target-hidden", 404)]
    public async Task MoveBinding_InvalidOrGuarded_EmitsNeitherSuccessPath(string scenario, int status)
    {
        using var fixture = new MoveFixture(false);
        await fixture.SeedAsync();
        var source = fixture.Source;
        var target = fixture.Target.Value;
        var binding = fixture.Moved.BindingId;
        if (scenario == "blank") target = " ";
        if (scenario == "same") target = source.Value;
        if (scenario == "missing-source") source = ConversationId.Create();
        if (scenario == "missing-target") target = ConversationId.Create().Value;
        if (scenario == "missing-binding") binding = BindingId.Create();
        if (scenario == "cross-agent")
        {
            var other = NewConversation(AgentId.From("other-agent"));
            await fixture.Store.CreateAsync(other);
            target = other.ConversationId.Value;
        }
        if (scenario == "collision") await fixture.Store.AddBindingAsync(fixture.Target, NewBinding("moved"));
        if (scenario.EndsWith("readonly", StringComparison.Ordinal) || scenario.EndsWith("hidden", StringComparison.Ordinal))
        {
            var guarded = new Conversation
            {
                ConversationId = ConversationId.Create(), AgentId = Agent,
                Visibility = scenario.EndsWith("readonly", StringComparison.Ordinal)
                    ? ConversationVisibility.InspectableReadOnly : ConversationVisibility.InternalHidden,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            await fixture.Store.CreateAsync(guarded);
            if (scenario.StartsWith("source", StringComparison.Ordinal))
            {
                source = guarded.ConversationId;
                await fixture.Store.AddBindingAsync(source, fixture.Moved);
            }
            else target = guarded.ConversationId.Value;
        }
        var boundary = ReadThrough(fixture.Store); // Strict: no mutation is permitted on any guard path.
        var sink = new CommittedSink(fixture);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new RecordingNotifier();
        var result = await Controller(boundary.Object, publisher, notifier).MoveBinding(source.Value, binding.Value,
            new MoveBindingRequest(target), CancellationToken.None);
        var actualStatus = result is ObjectResult obj ? obj.StatusCode : ((StatusCodeResult)result).StatusCode;
        actualStatus.ShouldBe(status);
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldBeEmpty();
        notifier.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MoveBinding_PublisherRejectsOrThrows_StillAttemptsOtherSideAndLegacy(bool failTarget, bool throws)
    {
        using var fixture = new MoveFixture(false);
        await fixture.SeedAsync();
        var sink = new CommittedSink(fixture);
        await using var realPublisher = new ConversationEventPublisher([sink]);
        var publisher = new FailingPublisher(realPublisher, failTarget ? fixture.Target : fixture.Source, throws);
        var notifier = new RecordingNotifier();
        (await Controller(fixture.Store, publisher, notifier).MoveBinding(fixture.Source.Value,
            fixture.Moved.BindingId.Value, new MoveBindingRequest(fixture.Target.Value), CancellationToken.None))
            .ShouldBeOfType<OkObjectResult>();
        await realPublisher.WaitForDrainAsync();
        publisher.Attempts.ShouldBe(new[] { fixture.Source, fixture.Target });
        sink.Events.ShouldHaveSingleItem().ConversationId.ShouldBe(failTarget ? fixture.Source : fixture.Target);
        sink.CommittedChecks.ShouldBe(1);
        AssertLegacy(notifier, fixture);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MoveBinding_PostCommitReadMissingOrThrows_IsolatesOtherSideAndLegacy(bool failTarget, bool throws)
    {
        using var fixture = new MoveFixture(false);
        await fixture.SeedAsync();
        var committed = false;
        var failedId = failTarget ? fixture.Target : fixture.Source;
        var boundary = new Mock<IConversationStore>(MockBehavior.Strict);
        boundary.Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .Returns<ConversationId, CancellationToken>((id, ct) => committed && id == failedId
                ? throws ? Task.FromException<Conversation?>(new InvalidOperationException("reload failed"))
                    : Task.FromResult<Conversation?>(null)
                : fixture.Store.GetAsync(id, ct));
        boundary.Setup(s => s.MoveBindingAsync(fixture.Source, fixture.Target, fixture.Moved.BindingId, It.IsAny<CancellationToken>()))
            .Returns<ConversationId, ConversationId, BindingId, CancellationToken>(async (from, to, binding, ct) =>
            {
                var moved = await fixture.Store.MoveBindingAsync(from, to, binding, ct);
                committed = true;
                return moved;
            });
        var sink = new CommittedSink(fixture);
        await using var publisher = new ConversationEventPublisher([sink]);
        var notifier = new RecordingNotifier();
        (await Controller(boundary.Object, publisher, notifier).MoveBinding(fixture.Source.Value,
            fixture.Moved.BindingId.Value, new MoveBindingRequest(fixture.Target.Value), CancellationToken.None))
            .ShouldBeOfType<OkObjectResult>();
        await publisher.WaitForDrainAsync();
        sink.Events.ShouldHaveSingleItem().ConversationId.ShouldBe(failTarget ? fixture.Source : fixture.Target);
        sink.CommittedChecks.ShouldBe(1);
        AssertLegacy(notifier, fixture);
    }

    private static Mock<IConversationStore> ReadThrough(IConversationStore store)
    {
        var mock = new Mock<IConversationStore>(MockBehavior.Strict);
        mock.Setup(s => s.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>()))
            .Returns<ConversationId, CancellationToken>((id, ct) => store.GetAsync(id, ct));
        return mock;
    }

    private static ConversationsController Controller(IConversationStore store, IConversationEventPublisher publisher,
        IConversationChangeNotifier notifier) => new(store, new InMemorySessionStore(),
            conversationChangeNotifiers: [notifier], conversationEventPublisher: publisher);

    private static void AssertLegacy(RecordingNotifier notifier, MoveFixture fixture) => notifier.Calls.ShouldBe(
        new[] { ("updated", Agent.Value, fixture.Source.Value), ("updated", Agent.Value, fixture.Target.Value) });

    private static Conversation NewConversation(AgentId agent) => new()
    {
        ConversationId = ConversationId.Create(), AgentId = agent, Title = "move facts",
        Status = ConversationStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ChannelBinding NewBinding(string address) => new()
    {
        BindingId = BindingId.Create(), ChannelType = ChannelKey.From("telegram"),
        ChannelAddress = ChannelAddress.From(address), Mode = BindingMode.NotifyOnly,
        ThreadingMode = ThreadingMode.Prefix, DisplayPrefix = "move-prefix", BoundAt = DateTimeOffset.UtcNow
    };

    private sealed class MoveFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-move-{Guid.NewGuid():N}");
        private readonly string? _connectionString;
        public IConversationStore Store { get; }
        public ConversationId Source { get; private set; }
        public ConversationId Target { get; private set; }
        public ChannelBinding Moved { get; } = NewBinding("moved");
        public MoveFixture(bool sqlite)
        {
            if (sqlite)
            {
                Directory.CreateDirectory(_directory);
                _connectionString = new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(_directory, "conversations.db"), Pooling = false }.ToString();
                Store = SqliteStore();
            }
            else Store = new InMemoryConversationStore();
        }
        private SqliteConversationStore SqliteStore() => new(_connectionString ?? throw new InvalidOperationException(),
            NullLogger<SqliteConversationStore>.Instance);
        public Task<Conversation?> ReadFreshAsync(ConversationId id) =>
            (_connectionString is null ? Store : SqliteStore()).GetAsync(id);
        public async Task SeedAsync()
        {
            var source = NewConversation(Agent);
            var target = NewConversation(Agent);
            Source = source.ConversationId;
            Target = target.ConversationId;
            await Store.CreateAsync(source);
            await Store.CreateAsync(target);
            await Store.AddBindingAsync(Source, Moved);
            await Store.AddBindingAsync(Source, NewBinding("source-retained"));
            await Store.AddBindingAsync(Target, NewBinding("target-retained"));
        }
        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class CommittedSink(MoveFixture fixture) : IConversationEventSink
    {
        public List<ConversationEvent> Events { get; } = [];
        public int CommittedChecks { get; private set; }
        public async Task OnConversationEventAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            // Sink exceptions are isolated by the publisher: increment only after every assertion succeeds.
            var source = (await fixture.ReadFreshAsync(fixture.Source)).ShouldNotBeNull();
            var target = (await fixture.ReadFreshAsync(fixture.Target)).ShouldNotBeNull();
            source.ChannelBindings.ShouldNotContain(b => b.BindingId == fixture.Moved.BindingId);
            target.ChannelBindings.ShouldContain(b => b.BindingId == fixture.Moved.BindingId);
            lock (Events)
            {
                Events.Add(fact);
                CommittedChecks++;
            }
        }
    }

    private sealed class RecordingNotifier : IConversationChangeNotifier
    {
        public List<(string, string, string)> Calls { get; } = [];
        public Task NotifyConversationChangedAsync(string changeType, string agentId, string conversationId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((changeType, agentId, conversationId));
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPublisher(IConversationEventPublisher inner, ConversationId failedId, bool throws)
        : IConversationEventPublisher
    {
        public List<ConversationId> Attempts { get; } = [];
        public ValueTask<bool> PublishAsync(ConversationEvent fact, CancellationToken cancellationToken = default)
        {
            Attempts.Add(fact.ConversationId);
            if (fact.ConversationId != failedId) return inner.PublishAsync(fact, cancellationToken);
            if (throws) throw new InvalidOperationException("publication failed");
            return ValueTask.FromResult(false);
        }
        public Task WaitForDrainAsync(CancellationToken cancellationToken = default) => inner.WaitForDrainAsync(cancellationToken);
    }
}
