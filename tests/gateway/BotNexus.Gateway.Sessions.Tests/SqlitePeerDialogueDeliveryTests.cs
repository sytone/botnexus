using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Contracts.Agents;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Sessions.Tests;

public sealed class SqlitePeerDialogueDeliveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "peer-delivery-tests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private static readonly AgentId Alice = AgentId.From("alice");
    private static readonly AgentId Bob = AgentId.From("bob");
    private static readonly PeerDialogueEndpoint A = new(Alice, ConversationId.From("a-chat"));
    private static readonly PeerDialogueEndpoint B = new(Bob, ConversationId.From("b-chat"));
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    public SqlitePeerDialogueDeliveryTests() => Directory.CreateDirectory(_directory);
    private SqlitePeerDialogueStore Store() => new(new SqliteConnectionStringBuilder
    { DataSource = Path.Combine(_directory, "delivery.db"), Pooling = false }.ToString(), _clock);
    private async Task<PeerDialogueMessageRequest> Seed(int budget = 4)
    {
        var spec = new PeerDialogueSpecification(ConversationId.Create(), A, B, A, budget, _clock.Now.AddMinutes(5));
        await Store().CreateAsync(Alice, spec);
        var request = new PeerDialogueMessageRequest(PeerDialogueMessageId.From(Guid.NewGuid()), spec.DialogueConversationId, A, B, null, "question");
        await Store().AdmitAsync(Alice, request);
        return request;
    }
    private async Task<PeerDialogueClaim> Claim(PeerDialogueMessageRequest request) =>
        (await Store().ClaimAsync(Bob, request.DialogueConversationId, request.MessageId, RunId.Create(), Lease))
        ?? throw new InvalidOperationException("Expected claim.");
    private Task<PeerDialogueDelivery> Read(PeerDialogueMessageRequest request) =>
        Store().GetDeliveryAsync(Bob, request.DialogueConversationId, request.MessageId);
    private static PeerDialogueCompletion Completion(bool success = true, string? output = "answer") =>
        new(success, output, PeerDialogueMessageId.From(Guid.NewGuid()));
    private static async Task Reject(Func<Task> action, PeerDialogueRejection reason)
    {
        (await Should.ThrowAsync<PeerDialogueException>(action)).Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task Complete_Reopen_AtomicallyAdmitsCurrentAuthoredReplyAndReconcilesOnce()
    {
        var request = await Seed();
        var claim = await Claim(request);
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Reserved);
        var started = await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        started.State.ShouldBe(PeerDialogueDeliveryState.Started);
        started.StartedAt.ShouldBe(_clock.Now);
        var completion = Completion(output: "  exact authored output  ");
        var result = await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion);
        result.State.ShouldBe(PeerDialogueDeliveryState.Completed);
        result.Completion.ShouldBe(completion);
        var reply = result.Reply.ShouldNotBeNull();
        reply.Request.Sender.ShouldBe(B);
        reply.Request.Recipient.ShouldBe(A);
        reply.Request.ReplyTo.ShouldBe(request.MessageId);
        reply.Request.Content.ShouldBe(completion.Output);
        reply.Sequence.ShouldBe(2);
        (await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion)).ShouldBe(result);
        (await Store().GetAsync(Alice, request.DialogueConversationId)).ReservedTurns.ShouldBe(2);
        (await Store().ListPendingAsync(Alice, request.DialogueConversationId, 10)).ShouldHaveSingleItem().ShouldBe(reply);
        await Reject(() => Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion with { Output = "changed" }), PeerDialogueRejection.Conflict);
    }

    [Fact]
    public async Task Reserved_LeaseExpiry_ReclaimsWithHigherGenerationAndFencesOldOwner()
    {
        var request = await Seed();
        var old = await Claim(request);
        (await Store().ClaimAsync(Bob, request.DialogueConversationId, request.MessageId, RunId.Create(), Lease)).ShouldBeNull();
        _clock.Now = old.LeaseExpiresAt;
        var current = await Claim(request);
        current.Generation.ShouldBe(old.Generation + 1);
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, old), PeerDialogueRejection.StaleClaim);
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, current with { Generation = old.Generation }), PeerDialogueRejection.StaleClaim);
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, current with { Owner = old.Owner }), PeerDialogueRejection.StaleClaim);
        await Store().StartAsync(Bob, request.DialogueConversationId, current);
        await Reject(() => Store().CompleteAsync(Bob, request.DialogueConversationId, old, Completion()), PeerDialogueRejection.StaleClaim);
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Started);
    }

    [Fact]
    public async Task Started_LeaseExpiry_IsUncertainAndNeverAutomaticallyReplayed()
    {
        var request = await Seed();
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        _clock.Now = claim.LeaseExpiresAt;
        (await Store().ClaimAsync(Bob, request.DialogueConversationId, request.MessageId, RunId.Create(), Lease)).ShouldBeNull();
        var evidence = await Read(request);
        evidence.State.ShouldBe(PeerDialogueDeliveryState.Uncertain);
        evidence.StartedAt.ShouldNotBeNull();
        evidence.Claim.ShouldBe(claim);
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, claim), PeerDialogueRejection.StaleClaim);
        await Reject(() => Store().CompleteAsync(Bob, request.DialogueConversationId, claim, Completion()), PeerDialogueRejection.StaleClaim);
        _clock.Now = _clock.Now.AddHours(1);
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Uncertain);
        (await Store().GetAsync(Alice, request.DialogueConversationId)).ReservedTurns.ShouldBe(1);
    }

    [Theory]
    [InlineData("closed", PeerDialogueRejection.Closed)]
    [InlineData("expired", PeerDialogueRejection.Expired)]
    [InlineData("exhausted", PeerDialogueRejection.Exhausted)]
    public async Task Complete_AdmissionStopped_RetainsActualOutputWithoutReply(string stop, PeerDialogueRejection reason)
    {
        var request = await Seed(stop == "exhausted" ? 1 : 4);
        // Complete after dialogue expiry, but before the independently bounded execution lease.
        if (stop == "expired") _clock.Now = _clock.Now.AddMinutes(4).AddSeconds(50);
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        if (stop == "closed") await Store().CloseAsync(Alice, request.DialogueConversationId);
        if (stop == "expired") _clock.Now = _clock.Now.AddSeconds(10);
        var completion = Completion();
        var result = await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion);
        result.State.ShouldBe(PeerDialogueDeliveryState.Completed);
        result.Completion.ShouldBe(completion);
        result.Reply.ShouldBeNull();
        result.ReplyRejection.ShouldBe(reason);
        (await Read(request)).ShouldBe(result);
        (await Store().ListPendingAsync(Alice, request.DialogueConversationId, 10)).ShouldBeEmpty();
        (await Store().GetAsync(Alice, request.DialogueConversationId)).ReservedTurns.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, "actual failed output")]
    [InlineData(true, " ")]
    public async Task Complete_FailureOrInvalidAuthoredOutput_NeverSynthesizesReply(bool succeeded, string output)
    {
        var request = await Seed();
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        var completion = Completion(succeeded, output);
        var result = await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion);
        result.Completion.ShouldBe(completion);
        result.Reply.ShouldBeNull();
        (await Store().GetAsync(Bob, request.DialogueConversationId)).ReservedTurns.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimAndStart_RecheckCloseOrExpiry(bool expire)
    {
        var request = await Seed();
        var claim = await Claim(request);
        if (expire) _clock.Now = _clock.Now.AddMinutes(5);
        else await Store().CloseAsync(Alice, request.DialogueConversationId);
        var reason = expire ? PeerDialogueRejection.Expired : PeerDialogueRejection.Closed;
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, claim), reason);
        await Reject(() => Store().ClaimAsync(Bob, request.DialogueConversationId, request.MessageId, RunId.Create(), Lease), reason);
        (await Read(request)).StartedAt.ShouldBeNull();
        (await Store().ListPendingAsync(Bob, request.DialogueConversationId, 10)).ShouldHaveSingleItem(); // Admission evidence remains available.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_SuppressesUnstartedWorkAndPreservesStartedUncertainty(bool started)
    {
        var request = await Seed();
        var claim = await Claim(request);
        if (started) await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        await Store().CancelAsync(Alice, request.DialogueConversationId, request.MessageId);
        (await Read(request)).State.ShouldBe(started ? PeerDialogueDeliveryState.Uncertain : PeerDialogueDeliveryState.Cancelled);
        (await Store().ClaimAsync(Bob, request.DialogueConversationId, request.MessageId, RunId.Create(), Lease)).ShouldBeNull();
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, claim), PeerDialogueRejection.StaleClaim);
        await Reject(() => Store().CompleteAsync(Bob, request.DialogueConversationId, claim, Completion()), PeerDialogueRejection.StaleClaim);
    }

    [Fact]
    public async Task CompletionFaultAndCancellation_RollBackReplyBudgetAndAcknowledgementTogether()
    {
        var request = await Seed();
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        var completion = Completion();
        var store = Store();
        store.BeforeCompletionCommitAsync = async ct =>
        {
            (await Store().GetAsync(Bob, request.DialogueConversationId, ct)).ReservedTurns.ShouldBe(1);
            (await Store().ListPendingAsync(Alice, request.DialogueConversationId, 10, cancellationToken: ct)).ShouldBeEmpty();
            throw new IOException("rollback boundary");
        };
        await Should.ThrowAsync<IOException>(() => store.CompleteAsync(Bob, request.DialogueConversationId, claim, completion));
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Started);
        using var cancellation = new CancellationTokenSource();
        store.BeforeCompletionCommitAsync = ct => { cancellation.Cancel(); return Task.CompletedTask; };
        await Should.ThrowAsync<OperationCanceledException>(() => store.CompleteAsync(Bob, request.DialogueConversationId, claim, completion, cancellation.Token));
        (await Read(request)).Completion.ShouldBeNull();
        (await Store().GetAsync(Bob, request.DialogueConversationId)).ReservedTurns.ShouldBe(1);
        (await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion)).Reply.ShouldNotBeNull();
    }

    [Fact]
    public async Task Claim_ParallelStores_OnlyOneOwnerAndRecipientMayExecute()
    {
        var request = await Seed();
        using var barrier = new Barrier(2);
        Task<PeerDialogueClaim?> Attempt() => Task.Run(async () =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(15)).ShouldBeTrue();
            return await Store().ClaimAsync(Bob, request.DialogueConversationId, request.MessageId, RunId.Create(), Lease);
        });
        var results = await Task.WhenAll(Attempt(), Attempt());
        var claim = results.Where(x => x is not null).ShouldHaveSingleItem().ShouldNotBeNull();
        await Reject(() => Store().ClaimAsync(Alice, request.DialogueConversationId, request.MessageId, RunId.Create(), Lease), PeerDialogueRejection.Unauthorized);
        await Reject(() => Store().StartAsync(Alice, request.DialogueConversationId, claim), PeerDialogueRejection.Unauthorized);
        await Reject(() => Store().CompleteAsync(Alice, request.DialogueConversationId, claim, Completion()), PeerDialogueRejection.Unauthorized);
        foreach (var lease in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromMinutes(6) })
            await Reject(() => Store().ClaimAsync(Bob, request.DialogueConversationId, request.MessageId, RunId.Create(), lease), PeerDialogueRejection.Invalid);
        await Reject(() => Store().CompleteAsync(Bob, request.DialogueConversationId, claim, Completion()), PeerDialogueRejection.StaleClaim);
    }

    [Fact]
    public async Task Complete_ConcurrentEqualRetries_ReserveOneReplyAndKeepOneAcknowledgement()
    {
        var request = await Seed(2);
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        var completion = Completion();
        using var barrier = new Barrier(2);
        Task<PeerDialogueDelivery> Attempt() => Task.Run(async () =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(15)).ShouldBeTrue();
            return await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion);
        });
        var results = await Task.WhenAll(Attempt(), Attempt());
        results[0].ShouldBe(results[1]);
        results[0].Reply.ShouldNotBeNull();
        (await Store().GetAsync(Bob, request.DialogueConversationId)).ReservedTurns.ShouldBe(2);
        (await Store().ListPendingAsync(Alice, request.DialogueConversationId, 10)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Complete_RacesOrdinaryAdmissionForLastSlot_BudgetRemainsShared()
    {
        var request = await Seed(2);
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        var competing = request with { MessageId = PeerDialogueMessageId.From(Guid.NewGuid()), Content = "competing" };
        using var barrier = new Barrier(2);
        var complete = Task.Run(async () =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(15)).ShouldBeTrue();
            return await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, Completion());
        });
        var admission = Task.Run(async () =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(15)).ShouldBeTrue();
            try { await Store().AdmitAsync(Alice, competing); return true; }
            catch (PeerDialogueException ex) when (ex.Reason == PeerDialogueRejection.Exhausted) { return false; }
        });
        await Task.WhenAll(complete, admission);
        var completed = await complete;
        var admitted = await admission;
        completed.State.ShouldBe(PeerDialogueDeliveryState.Completed);
        (completed.Reply is not null).ShouldBe(!admitted);
        completed.ReplyRejection.ShouldBe(admitted ? PeerDialogueRejection.Exhausted : null);
        (await Store().GetAsync(Alice, request.DialogueConversationId)).ReservedTurns.ShouldBe(2);
    }

    [Fact]
    public async Task Complete_ReplyIdentityCollision_RetainsOutputWithoutReusingOrChangingExistingMessage()
    {
        var request = await Seed();
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        var completion = Completion() with { ReplyMessageId = request.MessageId };
        var result = await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion);
        result.ReplyRejection.ShouldBe(PeerDialogueRejection.Conflict);
        result.Reply.ShouldBeNull();
        result.Completion.ShouldBe(completion);
        (await Store().AdmitAsync(Alice, request)).Request.ShouldBe(request);
        (await Store().GetAsync(Alice, request.DialogueConversationId)).ReservedTurns.ShouldBe(1);
    }

    [Fact]
    public async Task Delivery_ForeignDialogueAndUnauthorizedMember_CannotReadOrCancelEvidence()
    {
        var request = await Seed();
        var other = await Seed();
        var outsider = AgentId.From("outsider");
        await Reject(() => Store().GetDeliveryAsync(outsider, request.DialogueConversationId, request.MessageId), PeerDialogueRejection.Unauthorized);
        await Reject(() => Store().CancelAsync(outsider, request.DialogueConversationId, request.MessageId), PeerDialogueRejection.Unauthorized);
        await Reject(() => Store().GetDeliveryAsync(Bob, other.DialogueConversationId, request.MessageId), PeerDialogueRejection.NotFound);
        await Reject(() => Store().CancelAsync(Bob, other.DialogueConversationId, request.MessageId), PeerDialogueRejection.NotFound);
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Pending);
    }

    [Fact]
    public async Task Start_ExpiredReservedLease_RejectsWithoutCreatingStartedEvidence()
    {
        var request = await Seed();
        var claim = await Claim(request);
        _clock.Now = claim.LeaseExpiresAt;
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, claim), PeerDialogueRejection.StaleClaim);
        (await Read(request)).StartedAt.ShouldBeNull();
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Reserved);
    }

    [Fact]
    public async Task Complete_OversizedActualOutput_IsRetainedButNeverAdmittedAsReply()
    {
        var request = await Seed();
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        var completion = Completion(output: new string('x', PeerDialogueMessageRequest.MaxContentLength + 1));
        var result = await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion);
        result.Completion.ShouldBe(completion);
        result.Reply.ShouldBeNull();
        result.ReplyRejection.ShouldBe(PeerDialogueRejection.Invalid);
        (await Read(request)).Completion.ShouldBe(completion);
        (await Store().GetAsync(Alice, request.DialogueConversationId)).ReservedTurns.ShouldBe(1);
    }

    [Fact]
    public async Task Start_SecondAttemptCannotAuthorizeAnotherRuntimeInvocation()
    {
        var request = await Seed();
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, claim), PeerDialogueRejection.StaleClaim);
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Started);
        (await Read(request)).Claim.ShouldBe(claim);
    }

    [Fact]
    public async Task Schema1_Upgrade_PreservesAdmissionBudgetAndSupportsFencedCompletion()
    {
        var spec = new PeerDialogueSpecification(ConversationId.Create(), A, B, A, 4, _clock.Now.AddMinutes(5));
        var request = new PeerDialogueMessageRequest(PeerDialogueMessageId.From(Guid.NewGuid()), spec.DialogueConversationId, A, B, null, "legacy question");
        var message = new PeerDialogueMessage(request, 1, _clock.Now, PeerDialogueDeliveryState.Pending);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "delivery.db"), Pooling = false }.ToString();
        await using (var connection = BotNexus.Persistence.Sqlite.SqliteConnectionFactory.CreateForStoreKind(connectionString, "peer-dialogues"))
        {
            await connection.OpenAsync();
            BotNexus.Persistence.Sqlite.SqliteSchemaMigrator.Apply(connection, 1, []);
            using var command = connection.CreateCommand();
            // Exact foundation table shape, with no lifecycle table.
            command.CommandText = """
                CREATE TABLE peer_dialogues(dialogue_id TEXT PRIMARY KEY NOT NULL, specification TEXT NOT NULL,
                    reserved_turns INTEGER NOT NULL CHECK(reserved_turns >= 0), is_closed INTEGER NOT NULL CHECK(is_closed IN (0,1)));
                CREATE TABLE peer_messages(message_id TEXT PRIMARY KEY NOT NULL,
                    dialogue_id TEXT NOT NULL REFERENCES peer_dialogues(dialogue_id), sequence INTEGER NOT NULL CHECK(sequence > 0),
                    recipient_agent TEXT NOT NULL, delivery_state INTEGER NOT NULL CHECK(delivery_state = 0),
                    message TEXT NOT NULL, UNIQUE(dialogue_id,sequence));
                CREATE INDEX ix_peer_pending ON peer_messages(dialogue_id,recipient_agent,delivery_state,sequence);
                INSERT INTO peer_dialogues VALUES($dialogue,$spec,1,0);
                INSERT INTO peer_messages VALUES($messageId,$dialogue,1,$recipient,0,$message);
                """;
            command.Parameters.AddWithValue("$dialogue", spec.DialogueConversationId.Value);
            command.Parameters.AddWithValue("$spec", System.Text.Json.JsonSerializer.Serialize(spec));
            command.Parameters.AddWithValue("$messageId", request.MessageId.Value.ToString("N"));
            command.Parameters.AddWithValue("$recipient", Bob.Value);
            command.Parameters.AddWithValue("$message", System.Text.Json.JsonSerializer.Serialize(message));
            await command.ExecuteNonQueryAsync();
        }
        (await Read(request)).Message.ShouldBe(message);
        (await Store().AdmitAsync(Alice, request)).ShouldBe(message);
        (await Store().GetAsync(Alice, request.DialogueConversationId)).ReservedTurns.ShouldBe(1);
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        var completed = await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, Completion());
        completed.Reply.ShouldNotBeNull().Sequence.ShouldBe(2);
        (await Read(request)).ShouldBe(completed);
        await using var upgraded = new SqliteConnection(connectionString);
        await upgraded.OpenAsync();
        using var version = upgraded.CreateCommand();
        version.CommandText = "SELECT value FROM store_meta WHERE key='schema_version'";
        (await version.ExecuteScalarAsync()).ShouldBe("2");
        version.CommandText = "PRAGMA user_version";
        Convert.ToInt32(await version.ExecuteScalarAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Reserved_ReusedOwnerStillRequiresNewGenerationAndExactLease()
    {
        var request = await Seed();
        var old = await Claim(request);
        _clock.Now = old.LeaseExpiresAt;
        var current = (await Store().ClaimAsync(Bob, request.DialogueConversationId, request.MessageId, old.Owner, Lease)).ShouldNotBeNull();
        current.Generation.ShouldBe(old.Generation + 1);
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, old), PeerDialogueRejection.StaleClaim);
        await Reject(() => Store().StartAsync(Bob, request.DialogueConversationId, current with { LeaseExpiresAt = current.LeaseExpiresAt.AddTicks(1) }), PeerDialogueRejection.StaleClaim);
        await Store().StartAsync(Bob, request.DialogueConversationId, current);
    }

    [Fact]
    public async Task Completed_RetryAfterCloseExpiryAndCancel_PreservesExactEvidence()
    {
        var request = await Seed();
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        var completion = Completion();
        var result = await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion);
        await Store().CloseAsync(Alice, request.DialogueConversationId);
        _clock.Now = _clock.Now.AddHours(1);
        await Store().CancelAsync(Alice, request.DialogueConversationId, request.MessageId);
        (await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion)).ShouldBe(result);
        (await Read(request)).ShouldBe(result);
        (await Store().GetAsync(Alice, request.DialogueConversationId)).ReservedTurns.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryApis_ForeignDialogueAndOutsider_CannotExecute(bool outsider)
    {
        var request = await Seed();
        var other = await Seed();
        var claim = await Claim(request);
        var agent = outsider ? AgentId.From("outsider") : Bob;
        var dialogue = outsider ? request.DialogueConversationId : other.DialogueConversationId;
        var reason = outsider ? PeerDialogueRejection.Unauthorized : PeerDialogueRejection.NotFound;
        await Reject(() => Store().ClaimAsync(agent, dialogue, request.MessageId, RunId.Create(), Lease), reason);
        await Reject(() => Store().StartAsync(agent, dialogue, claim), reason);
        await Reject(() => Store().CompleteAsync(agent, dialogue, claim, Completion()), reason);
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Reserved);
    }

    [Fact]
    public async Task Complete_MalformedUtf16_RejectsWithoutLosingLiveStartedEvidence()
    {
        var request = await Seed();
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        foreach (var output in new[] { "\ud800", "\udc00", "x\ud800y" })
            await Reject(() => Store().CompleteAsync(Bob, request.DialogueConversationId, claim, Completion(output: output)), PeerDialogueRejection.Invalid);
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Started);
        var completion = Completion(output: "valid \ud83d\ude80 output");
        (await Store().CompleteAsync(Bob, request.DialogueConversationId, claim, completion)).Reply.ShouldNotBeNull().Request.Content.ShouldBe(completion.Output);
    }

    [Fact]
    public async Task GetDelivery_ExpiredStartedLeaseAfterClose_MaterializesUncertainEvidence()
    {
        var request = await Seed();
        var claim = await Claim(request);
        await Store().StartAsync(Bob, request.DialogueConversationId, claim);
        await Store().CloseAsync(Alice, request.DialogueConversationId);
        _clock.Now = _clock.Now.AddHours(1);
        var result = await Read(request);
        result.State.ShouldBe(PeerDialogueDeliveryState.Uncertain);
        result.Claim.ShouldBe(claim);
        result.StartedAt.ShouldNotBeNull();
        result.Completion.ShouldBeNull();
        result.Reply.ShouldBeNull();
    }

    [Fact]
    public async Task Cancel_PendingAndRepeatedCancellation_NeverReleasesAdmissionBudget()
    {
        var request = await Seed();
        await Store().CancelAsync(Alice, request.DialogueConversationId, request.MessageId);
        await Store().CancelAsync(Bob, request.DialogueConversationId, request.MessageId);
        (await Read(request)).State.ShouldBe(PeerDialogueDeliveryState.Cancelled);
        (await Read(request)).Claim.ShouldBeNull();
        (await Store().ClaimAsync(Bob, request.DialogueConversationId, request.MessageId, RunId.Create(), Lease)).ShouldBeNull();
        (await Store().GetAsync(Alice, request.DialogueConversationId)).ReservedTurns.ShouldBe(1);
        (await Store().AdmitAsync(Alice, request)).Request.ShouldBe(request);
    }

    public void Dispose() => Directory.Delete(_directory, true);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
