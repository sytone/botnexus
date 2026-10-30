using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Contracts.Agents;
using BotNexus.Gateway.Sessions;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions.Tests;

public sealed class SqlitePeerDialogueStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "peer-dialogue-tests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private static readonly AgentId Alice = AgentId.From("alice");
    private static readonly AgentId Bob = AgentId.From("bob");
    private static readonly AgentId Mallory = AgentId.From("mallory");
    private static readonly PeerDialogueEndpoint A = new(Alice, ConversationId.From("alice-chat"));
    private static readonly PeerDialogueEndpoint B = new(Bob, ConversationId.From("bob-chat"));

    public SqlitePeerDialogueStoreTests() => Directory.CreateDirectory(_directory);
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "peers.db"), Pooling = false }.ToString();
    private SqlitePeerDialogueStore Store() => new(ConnectionString, _clock);
    private PeerDialogueSpecification Spec(int max = 4) => new(ConversationId.Create(), A, B, A, max, _clock.GetUtcNow().AddMinutes(5));
    private static PeerDialogueMessageRequest Message(PeerDialogueSpecification spec, string content = "hello") =>
        new(PeerDialogueMessageId.From(Guid.NewGuid()), spec.DialogueConversationId, A, B, null, content);

    [Fact]
    public async Task Admission_Reopen_ReconcilesStablePendingWithoutSecondReservation()
    {
        var spec = Spec();
        var request = Message(spec);
        var store = Store();
        await store.CreateAsync(Alice, spec);
        var first = await store.AdmitAsync(Alice, request);
        var reopened = Store(); // Each operation disposes its real SQLite connection.
        (await reopened.CreateAsync(Alice, spec)).Specification.ShouldBe(spec);
        (await reopened.AdmitAsync(Alice, request)).ShouldBe(first);
        first.Sequence.ShouldBe(1);
        first.DeliveryState.ShouldBe(PeerDialogueDeliveryState.Pending);
        (await reopened.GetAsync(Bob, spec.DialogueConversationId)).ReservedTurns.ShouldBe(1);
        (await reopened.ListPendingAsync(Bob, spec.DialogueConversationId, 10)).ShouldHaveSingleItem().ShouldBe(first);
        (await reopened.ListPendingAsync(Alice, spec.DialogueConversationId, 10)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Replays_ChangedImmutableFields_ConflictAndDoNotExposeContent()
    {
        var store = Store();
        var spec = Spec();
        await store.CreateAsync(Alice, spec);
        var request = Message(spec, "PRIVATE-CONTENT");
        await store.AdmitAsync(Alice, request);
        foreach (var changed in new[] { spec with { MaxAutomaticMessages = 5 }, spec with { Origin = B }, spec with { ExpiresAt = spec.ExpiresAt.AddTicks(1) }, spec with { Second = B with { ConversationId = ConversationId.Create() } } })
            await Reject(() => store.CreateAsync(Alice, changed), PeerDialogueRejection.Conflict);
        foreach (var changed in new[] { request with { Content = "changed" }, request with { ReplyTo = PeerDialogueMessageId.From(Guid.NewGuid()) } })
        {
            var error = await Reject(() => store.AdmitAsync(Alice, changed), PeerDialogueRejection.Conflict);
            error.ToString().ShouldNotContain("PRIVATE-CONTENT");
        }
        var other = Spec();
        await store.CreateAsync(Alice, other);
        await Reject(() => store.AdmitAsync(Alice, request with { DialogueConversationId = other.DialogueConversationId }), PeerDialogueRejection.Conflict);
        (await store.GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(1);
    }

    [Fact]
    public async Task Authentication_SpoofForeignEndpointAndUnauthorizedReads_AreRejected()
    {
        var store = Store();
        var spec = Spec();
        await store.CreateAsync(Alice, spec);
        var request = Message(spec);
        await Reject(() => store.AdmitAsync(Bob, request), PeerDialogueRejection.Unauthorized);
        await Reject(() => store.AdmitAsync(Mallory, request), PeerDialogueRejection.Unauthorized);
        await Reject(() => store.AdmitAsync(Alice, request with { Recipient = A }), PeerDialogueRejection.Invalid);
        await Reject(() => store.AdmitAsync(Alice, request with { Sender = A with { ConversationId = ConversationId.Create() } }), PeerDialogueRejection.Unauthorized);
        await Reject(() => store.GetAsync(Mallory, spec.DialogueConversationId), PeerDialogueRejection.Unauthorized);
        await Reject(() => store.ListPendingAsync(Mallory, spec.DialogueConversationId, 10), PeerDialogueRejection.Unauthorized);
        await Reject(() => store.CloseAsync(Mallory, spec.DialogueConversationId), PeerDialogueRejection.Unauthorized);
        await Reject(() => store.CreateAsync(Bob, spec), PeerDialogueRejection.Unauthorized);
        (await store.GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(0);
    }

    [Fact]
    public async Task ReplyTo_RequiresOppositeAuthorSameDialogueAndAddressedEndpoint()
    {
        var store = Store();
        var spec = Spec();
        var other = Spec();
        await store.CreateAsync(Alice, spec);
        await store.CreateAsync(Alice, other);
        var root = await store.AdmitAsync(Alice, Message(spec));
        var foreign = await store.AdmitAsync(Alice, Message(other));
        await Reject(() => store.AdmitAsync(Alice, Message(spec) with { ReplyTo = root.Request.MessageId }), PeerDialogueRejection.Invalid);
        var reply = Message(spec) with { Sender = B, Recipient = A, ReplyTo = foreign.Request.MessageId };
        await Reject(() => store.AdmitAsync(Bob, reply), PeerDialogueRejection.Invalid);
        await Reject(() => store.AdmitAsync(Bob, reply with { ReplyTo = PeerDialogueMessageId.From(Guid.NewGuid()) }), PeerDialogueRejection.Invalid);
        var accepted = await store.AdmitAsync(Bob, reply with { ReplyTo = root.Request.MessageId });
        accepted.Sequence.ShouldBe(2);
        accepted.Request.ReplyTo.ShouldBe(root.Request.MessageId);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("expired")]
    [InlineData("exhausted")]
    public async Task TerminalDialogue_NewWorkRejectedButAcceptedReplayReconciles(string state)
    {
        var store = Store();
        var spec = Spec(state == "exhausted" ? 1 : 4);
        await store.CreateAsync(Alice, spec);
        var request = Message(spec);
        var first = await store.AdmitAsync(Alice, request);
        var reason = PeerDialogueRejection.Exhausted;
        if (state == "closed")
        {
            await store.CloseAsync(Bob, spec.DialogueConversationId);
            await store.CloseAsync(Alice, spec.DialogueConversationId);
            reason = PeerDialogueRejection.Closed;
        }
        if (state == "expired")
        {
            _clock.Now = spec.ExpiresAt;
            reason = PeerDialogueRejection.Expired;
        }
        await Reject(() => Store().AdmitAsync(Alice, Message(spec)), reason);
        (await Store().AdmitAsync(Alice, request)).ShouldBe(first);
        (await Store().CreateAsync(Alice, spec)).Specification.ShouldBe(spec);
        (await store.GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(1);
    }

    [Fact]
    public async Task ParallelStores_LastSlot_OnlyOneReservationAndSequence()
    {
        var spec = Spec(1);
        await Store().CreateAsync(Alice, spec);
        using var ready = new Barrier(2);
        async Task<PeerDialogueMessage?> Attempt()
        {
            return await Task.Run(async () =>
            {
                ready.SignalAndWait(TimeSpan.FromSeconds(15)).ShouldBeTrue();
                try { return await Store().AdmitAsync(Alice, Message(spec)); }
                catch (PeerDialogueException ex) when (ex.Reason == PeerDialogueRejection.Exhausted) { return null; }
            });
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        results.Count(x => x is not null).ShouldBe(1);
        (await Store().GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(1);
        (await Store().ListPendingAsync(Bob, spec.DialogueConversationId, 10)).ShouldHaveSingleItem().Sequence.ShouldBe(1);
    }

    [Fact]
    public async Task BeforeCommit_ObserverSeesNeitherWrite_FaultRollsBackBoth()
    {
        var store = Store();
        var spec = Spec(1);
        await store.CreateAsync(Alice, spec);
        var request = Message(spec);
        store.BeforeAdmissionCommitAsync = async ct =>
        {
            (await Store().GetAsync(Alice, spec.DialogueConversationId, ct)).ReservedTurns.ShouldBe(0);
            (await Store().ListPendingAsync(Bob, spec.DialogueConversationId, 10, cancellationToken: ct)).ShouldBeEmpty();
            throw new IOException("injected failure");
        };
        await Should.ThrowAsync<IOException>(() => store.AdmitAsync(Alice, request));
        (await Store().GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(0);
        (await Store().ListPendingAsync(Bob, spec.DialogueConversationId, 10)).ShouldBeEmpty();
        (await Store().AdmitAsync(Alice, request)).Sequence.ShouldBe(1);
    }

    [Fact]
    public async Task Cancellation_BeforeCommit_RollsBackAndDoesNotConsumeMessageId()
    {
        var store = Store();
        var spec = Spec(1);
        await store.CreateAsync(Alice, spec);
        using var cancellation = new CancellationTokenSource();
        store.BeforeAdmissionCommitAsync = ct => { cancellation.Cancel(); return Task.CompletedTask; };
        var request = Message(spec);
        await Should.ThrowAsync<OperationCanceledException>(() => store.AdmitAsync(Alice, request, cancellation.Token));
        (await Store().GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(0);
        (await Store().AdmitAsync(Alice, request)).Sequence.ShouldBe(1);
        await Should.ThrowAsync<OperationCanceledException>(() => Store().CloseAsync(Alice, spec.DialogueConversationId, cancellation.Token));
        (await Store().GetAsync(Alice, spec.DialogueConversationId)).IsClosed.ShouldBeFalse();
    }

    [Fact]
    public async Task Pending_BoundedRecipientScopedAndSequenceOrdered()
    {
        var store = Store();
        var spec = Spec();
        await store.CreateAsync(Alice, spec);
        for (var i = 0; i < 3; i++) await store.AdmitAsync(Alice, Message(spec));
        (await store.ListPendingAsync(Bob, spec.DialogueConversationId, 2)).Select(x => x.Sequence).ShouldBe(new long[] { 1, 2 });
        (await store.ListPendingAsync(Bob, spec.DialogueConversationId, 2, afterSequence: 2)).ShouldHaveSingleItem().Sequence.ShouldBe(3);
        await Reject(() => store.ListPendingAsync(Bob, spec.DialogueConversationId, 0), PeerDialogueRejection.Invalid);
        await Reject(() => store.ListPendingAsync(Bob, spec.DialogueConversationId, 101), PeerDialogueRejection.Invalid);
        await Reject(() => store.ListPendingAsync(Bob, spec.DialogueConversationId, 10, afterSequence: -1), PeerDialogueRejection.Invalid);
    }

    [Fact]
    public async Task Validation_InvalidSpecificationAndContent_DoNotPersist()
    {
        var store = Store();
        var spec = Spec();
        var oversizedId = new string('x', PeerDialogueSpecification.MaxEndpointIdentityLength + 1);
        foreach (var invalid in new[]
        {
            spec with { MaxAutomaticMessages = 0 }, spec with { MaxAutomaticMessages = -1 },
            spec with { ExpiresAt = _clock.Now }, spec with { Second = A },
            spec with { Origin = B with { ConversationId = ConversationId.Create() } },
            spec with { DialogueConversationId = ConversationId.From(oversizedId) },
            spec with { Second = B with { AgentId = AgentId.From(oversizedId) } },
            spec with { Second = B with { ConversationId = ConversationId.From(oversizedId) } }
        })
            await Reject(() => store.CreateAsync(Alice, invalid), PeerDialogueRejection.Invalid);
        await store.CreateAsync(Alice, spec);
        foreach (var content in new[] { "", "  ", new string('x', PeerDialogueMessageRequest.MaxContentLength + 1) })
            await Reject(() => store.AdmitAsync(Alice, Message(spec, content)), PeerDialogueRejection.Invalid);
        // An omitted wire ID creates an uninitialized value without constructing a forbidden
        // default in application code. The store must reject it before dereferencing Value.
        var wire = System.Text.Json.JsonSerializer.SerializeToNode(Message(spec))?.AsObject()
            ?? throw new InvalidOperationException("Expected message object.");
        wire.Remove(nameof(PeerDialogueMessageRequest.MessageId));
        var missingId = System.Text.Json.JsonSerializer.Deserialize<PeerDialogueMessageRequest>(wire)
            ?? throw new InvalidOperationException("Expected message request.");
        await Reject(() => store.AdmitAsync(Alice, missingId), PeerDialogueRejection.Invalid);
        (await store.GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(0);
    }

    [Fact]
    public async Task ParallelStores_EqualReplay_ReservesExactlyOnce()
    {
        var spec = Spec(1);
        await Store().CreateAsync(Alice, spec);
        var request = Message(spec);
        using var ready = new Barrier(2);
        Task<PeerDialogueMessage> Attempt() => Task.Run(async () =>
        {
            ready.SignalAndWait(TimeSpan.FromSeconds(15)).ShouldBeTrue();
            return await Store().AdmitAsync(Alice, request);
        });
        var results = await Task.WhenAll(Attempt(), Attempt());
        results[0].ShouldBe(results[1]);
        (await Store().GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(1);
    }

    [Fact]
    public async Task AlternatingSenders_ShareOneBudgetAndSequence()
    {
        var store = Store();
        var spec = Spec(2);
        await store.CreateAsync(Alice, spec);
        var first = await store.AdmitAsync(Alice, Message(spec));
        var reply = await store.AdmitAsync(Bob, Message(spec) with
        {
            Sender = B, Recipient = A, ReplyTo = first.Request.MessageId
        });
        reply.Sequence.ShouldBe(2);
        await Reject(() => store.AdmitAsync(Alice, Message(spec)), PeerDialogueRejection.Exhausted);
        await Reject(() => store.AdmitAsync(Bob, Message(spec) with { Sender = B, Recipient = A }), PeerDialogueRejection.Exhausted);
        (await Store().ListPendingAsync(Alice, spec.DialogueConversationId, 10)).ShouldHaveSingleItem().ShouldBe(reply);
        (await Store().GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(2);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public async Task MalformedUtf16_IsRejectedBeforeJsonCanChangeImmutableEvidence(int codeUnit)
    {
        // Construct after test-data transport so serialization cannot normalize the sentinel.
        var malformed = new string((char)codeUnit, 1);
        malformed.Length.ShouldBe(1);
        char.IsSurrogate(malformed[0]).ShouldBeTrue();
        var store = Store();
        var spec = Spec();
        await Reject(() => store.CreateAsync(Alice, spec with
        {
            DialogueConversationId = ConversationId.From("dialogue-" + malformed)
        }), PeerDialogueRejection.Invalid);
        await Reject(() => store.CreateAsync(Alice, spec with
        {
            Second = B with { AgentId = AgentId.From("agent-" + malformed) }
        }), PeerDialogueRejection.Invalid);
        await store.CreateAsync(Alice, spec);
        var request = Message(spec, "hello" + malformed);
        await Reject(() => store.AdmitAsync(Alice, request), PeerDialogueRejection.Invalid);
        (await store.GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(0);
        var valid = request with { Content = "hello\uFFFD" };
        var first = await store.AdmitAsync(Alice, valid);
        (await Store().AdmitAsync(Alice, valid)).ShouldBe(first);
        (await Store().ListPendingAsync(Bob, spec.DialogueConversationId, 10)).ShouldHaveSingleItem().Request.Content.ShouldBe(valid.Content);
    }

    [Fact]
    public async Task CreationReplay_OffsetOnlyChangeConflictsAfterReopen()
    {
        var spec = Spec();
        await Store().CreateAsync(Alice, spec);
        await Reject(() => Store().CreateAsync(Alice, spec with
        {
            ExpiresAt = spec.ExpiresAt.ToOffset(TimeSpan.FromHours(1))
        }), PeerDialogueRejection.Conflict);
        (await Store().GetAsync(Alice, spec.DialogueConversationId)).Specification.ExpiresAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task SeparateStore_SameIdentity_DoesNotLeakMessagesOrReservations()
    {
        var spec = Spec();
        await Store().CreateAsync(Alice, spec);
        await Store().AdmitAsync(Alice, Message(spec));
        var isolatedConnection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "isolated.db"), Pooling = false }.ToString();
        var isolated = new SqlitePeerDialogueStore(isolatedConnection, _clock);
        await isolated.CreateAsync(Alice, spec);
        (await isolated.GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(0);
        (await isolated.ListPendingAsync(Bob, spec.DialogueConversationId, 10)).ShouldBeEmpty();
        (await Store().GetAsync(Alice, spec.DialogueConversationId)).ReservedTurns.ShouldBe(1);
    }

    [Fact]
    public async Task NewerSchema_IsRefusedWithoutAdmission()
    {
        var spec = Spec();
        await Store().CreateAsync(Alice, spec);
        await using (var connection = SqliteConnectionFactory.CreateForStoreKind(ConnectionString, "peer-dialogues"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            var newerVersion = SqlitePeerDialogueStore.CurrentSchemaVersion + 1;
            command.CommandText = $"UPDATE store_meta SET value='{newerVersion}' WHERE key='schema_version'; PRAGMA user_version={newerVersion};";
            await command.ExecuteNonQueryAsync();
        }
        await Should.ThrowAsync<SqliteSchemaVersionMismatchException>(() => Store().AdmitAsync(Alice, Message(spec)));
    }

    private static async Task<PeerDialogueException> Reject(Func<Task> action, PeerDialogueRejection reason)
    {
        var error = await Should.ThrowAsync<PeerDialogueException>(action);
        error.Reason.ShouldBe(reason);
        return error;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
