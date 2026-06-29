using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Regression coverage for the #1628 bulk-insert hoist in
/// <c>SqliteConversationStore.AddParticipantsAsync</c>. The per-row
/// <c>conversation_participants</c> INSERT command and its parameters were hoisted out of
/// the foreach so only <c>.Value</c> is reset per row. A correct hoist must (a) keep the
/// <c>if (!participant.CitizenId.IsValid) continue;</c> skip, (b) reset every row-varying
/// parameter on every iteration, and (c) preserve INSERT OR IGNORE first-add-wins. These
/// tests add multiple participants of different kinds/roles, an invalid citizen (skipped),
/// and a re-add of an existing citizen, then reload and assert each participant's
/// kind/id/role round-trips - a stale shared parameter would corrupt a later row.
/// </summary>
public sealed class PrepareBulkInsertConversationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"bn-1628-conv-{Guid.NewGuid():N}.db");

    private SqliteConversationStore CreateStore()
        => new(
            $"Data Source={_dbPath};Pooling=False",
            NullLogger<SqliteConversationStore>.Instance);

    [Fact]
    public async Task AddParticipants_MixedKindsRolesAndInvalid_RoundTripsPerRow_SkipsInvalid()
    {
        var store = CreateStore();
        var convoId = ConversationId.Create();
        await store.CreateAsync(NewConversation(convoId, AgentId.From("owner")));

        // A batch spanning different CitizenKind + different roles, plus an INVALID citizen
        // (default(CitizenId) has Kind=Unknown / IsValid=false) which MUST be skipped by the
        // `continue` guard. The differing per-row values are the stale-parameter trap: an
        // un-reset $role / $citizenKind / $citizenId would corrupt a neighbouring row.
        await store.AddParticipantsAsync(convoId,
        [
            new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-alpha")), Role = "peer" },
            new SessionParticipant { CitizenId = CitizenId.Of(UserId.From("user-bob")), Role = "initiator" },
            new SessionParticipant { CitizenId = default, Role = "should-be-skipped" },
            new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-gamma")) },
        ]);

        var loaded = await CreateStore().GetAsync(convoId);
        loaded.ShouldNotBeNull();

        // The invalid citizen was skipped: 3 valid participants persisted, not 4.
        loaded!.Participants.Count.ShouldBe(3);

        var alpha = loaded.Participants.SingleOrDefault(p => p.CitizenId == CitizenId.Of(AgentId.From("agent-alpha")));
        alpha.ShouldNotBeNull();
        alpha!.CitizenId.Kind.ShouldBe(CitizenKind.Agent);
        alpha.Role.ShouldBe("peer");

        var bob = loaded.Participants.SingleOrDefault(p => p.CitizenId == CitizenId.Of(UserId.From("user-bob")));
        bob.ShouldNotBeNull();
        bob!.CitizenId.Kind.ShouldBe(CitizenKind.User);
        bob.Role.ShouldBe("initiator");

        var gamma = loaded.Participants.SingleOrDefault(p => p.CitizenId == CitizenId.Of(AgentId.From("agent-gamma")));
        gamma.ShouldNotBeNull();
        gamma!.CitizenId.Kind.ShouldBe(CitizenKind.Agent);
        // No role supplied -> null, and the "peer"/"initiator" roles from earlier rows must
        // NOT have bled into this row via a stale $role parameter.
        gamma.Role.ShouldBeNull();

        // The skipped invalid citizen left no trace.
        loaded.Participants.ShouldNotContain(p => p.Role == "should-be-skipped");
    }

    [Fact]
    public async Task AddParticipants_ReAddingExistingCitizen_InsertOrIgnore_KeepsFirstRole()
    {
        var store = CreateStore();
        var convoId = ConversationId.Create();
        await store.CreateAsync(NewConversation(convoId, AgentId.From("owner")));

        // First add wins on role: INSERT OR IGNORE against PK (conversation_id, citizen_kind,
        // citizen_id) preserves the original "first-role" when the same citizen is re-added
        // with a different role.
        await store.AddParticipantsAsync(convoId,
        [
            new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-alpha")), Role = "first-role" },
        ]);

        // Re-add the SAME citizen with a DIFFERENT role in a batch that also adds a brand-new
        // citizen. The existing row keeps "first-role"; the new citizen is inserted.
        await store.AddParticipantsAsync(convoId,
        [
            new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("agent-alpha")), Role = "second-role-ignored" },
            new SessionParticipant { CitizenId = CitizenId.Of(UserId.From("user-new")), Role = "newcomer" },
        ]);

        var loaded = await CreateStore().GetAsync(convoId);
        loaded.ShouldNotBeNull();
        loaded!.Participants.Count.ShouldBe(2);

        var alpha = loaded.Participants.SingleOrDefault(p => p.CitizenId == CitizenId.Of(AgentId.From("agent-alpha")));
        alpha.ShouldNotBeNull();
        alpha!.Role.ShouldBe("first-role");

        var newcomer = loaded.Participants.SingleOrDefault(p => p.CitizenId == CitizenId.Of(UserId.From("user-new")));
        newcomer.ShouldNotBeNull();
        newcomer!.CitizenId.Kind.ShouldBe(CitizenKind.User);
        newcomer.Role.ShouldBe("newcomer");
    }

    private static Conversation NewConversation(ConversationId id, AgentId agentId)
        => new()
        {
            ConversationId = id,
            AgentId = agentId,
            Title = "test-conv",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
