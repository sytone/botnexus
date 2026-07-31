using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Conversations;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Issue #2121, remaining clause: a conversation must carry the stable identity of the thing that
/// MINTED it - the webhook registration id for <see cref="ConversationSource.Webhook"/>, the cron
/// job id for <see cref="ConversationSource.Cron"/> - and that identifier must reach
/// <see cref="ConversationSummary"/> so a client can classify a
/// conversation without an extra per-feature list call.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Conversation.SourceId"/> is meaningless on its own: it is only interpretable in
/// combination with <see cref="Conversation.Source"/>. It is therefore held to exactly the same
/// write-once contract as <c>Source</c> - see
/// <see cref="Conversation_SourceId_IsWriteOnce_NoPublicSetter"/>. Splitting provenance across a
/// write-once discriminator and a mutable identifier would leave the pair re-poisonable by an
/// inbound event, which is precisely the failure mode epic #2300 removed.
/// </para>
/// <para>
/// The load-bearing back-compat test is
/// <see cref="Sqlite_LegacyRow_WithoutSourceIdColumn_LoadsAsNull"/>: every conversation row in a
/// user's database today predates this column, so the additive migration plus a null default must
/// hydrate those rows silently rather than throwing on first read.
/// </para>
/// </remarks>
public sealed class ConversationSourceIdTests
{
    // ---------------------------------------------------------------------
    // Domain: factory stamping
    // ---------------------------------------------------------------------

    [Fact]
    public void CreateForCron_StampsCronJobId_AsSourceId()
    {
        var conv = ConversationFactory.CreateForCron(
            ConversationId.Create(),
            AgentId.From("agent-a"),
            title: "nightly digest",
            sourceId: "job-42");

        conv.Source.ShouldBe(ConversationSource.Cron);
        conv.SourceId.ShouldBe("job-42",
            customMessage: "A cron-owned conversation must carry the cron job id so the portal can " +
                "attribute the run without a second list call (#2121).");
    }

    [Fact]
    public void CreateForWebhook_StampsRegistrationId_AsSourceId()
    {
        var conv = ConversationFactory.CreateForWebhook(
            ConversationId.Create(),
            AgentId.From("agent-a"),
            title: "Webhook: alerts",
            sourceId: "wh-7");

        conv.Source.ShouldBe(ConversationSource.Webhook);
        conv.SourceId.ShouldBe("wh-7",
            customMessage: "A webhook-created conversation must carry its webhook registration id (#2121).");
    }

    [Fact]
    public void CreateForCronAndWebhook_LeaveSourceIdNull_WhenNotSupplied()
    {
        // The parameter is optional so no existing caller breaks; an unstamped cron/webhook
        // conversation is an honest "origin known, originator not recorded" rather than a lie.
        ConversationFactory.CreateForCron(ConversationId.Create(), AgentId.From("agent-a"))
            .SourceId.ShouldBeNull();
        ConversationFactory.CreateForWebhook(ConversationId.Create(), AgentId.From("agent-a"))
            .SourceId.ShouldBeNull();
    }

    [Fact]
    public void CreateForChannelAndAgent_LeaveSourceIdNull()
    {
        // Sad path: SourceId is meaningful ONLY for Cron/Webhook. There is no id for "a human sent
        // a message" or "an agent minted this", and inventing one would make the field ambiguous.
        ConversationFactory.CreateForChannel(ConversationId.Create(), AgentId.From("agent-a"))
            .SourceId.ShouldBeNull(
                customMessage: "Channel-originated conversations have no minting registration/job id.");

        ConversationFactory.CreateForAgent(
            ConversationKind.AgentAgent,
            ConversationId.Create(),
            AgentId.From("agent-a"))
            .SourceId.ShouldBeNull(
                customMessage: "Agent-originated conversations have no minting registration/job id.");

        ConversationFactory.CreateForSubAgent(
            ConversationId.Create(),
            AgentId.From("agent-a"),
            ConversationId.Create())
            .SourceId.ShouldBeNull();
    }

    [Fact]
    public void Conversation_SourceId_DefaultsToNull_WhenNotStamped()
    {
        var conv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From("agent-a"),
            Title = "unstamped"
        };

        conv.SourceId.ShouldBeNull();
    }

    [Fact]
    public void Conversation_SourceId_IsWriteOnce_NoPublicSetter()
    {
        // Same reflective guard as Conversation.Source: `init` is distinguishable from `set` by the
        // IsExternalInit modreq on the setter's return parameter. SourceId is half of a provenance
        // pair; a plain setter would let an inbound event re-attribute a conversation to a
        // different webhook registration or cron job after the fact.
        var property = typeof(Conversation).GetProperty(nameof(Conversation.SourceId));
        property.ShouldNotBeNull();

        var setter = property!.SetMethod;
        setter.ShouldNotBeNull(
            customMessage: "SourceId must remain settable at construction time via an object initializer.");

        setter!.ReturnParameter.GetRequiredCustomModifiers()
            .ShouldContain(typeof(System.Runtime.CompilerServices.IsExternalInit),
                customMessage: "Conversation.SourceId must be init-only (#2121). It is one half of the " +
                    "write-once provenance pair with Source; a public setter would let an inbound event " +
                    "poison the originator identity that Source alone cannot express.");
    }

    // ---------------------------------------------------------------------
    // Persistence: round-trip
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(ConversationSource.Cron, "job-99")]
    [InlineData(ConversationSource.Webhook, "wh-abc")]
    public async Task Sqlite_SourceId_RoundTrips_AcrossProcessRestart(ConversationSource source, string sourceId)
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var conv = source == ConversationSource.Cron
            ? ConversationFactory.CreateForCron(ConversationId.Create(), AgentId.From("agent-a"), sourceId: sourceId)
            : ConversationFactory.CreateForWebhook(ConversationId.Create(), AgentId.From("agent-a"), sourceId: sourceId);

        await store.CreateAsync(conv);

        // Fresh store instance: proves the value survives at the SQL level rather than being
        // retained by the in-memory clone cache.
        var loaded = await fixture.CreateStore().GetAsync(conv.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.SourceId.ShouldBe(sourceId,
            customMessage: "SourceId must round-trip through the SQLite schema. A null here means the " +
                "INSERT or SELECT is missing the `source_id` binding and the field drops at the SQL boundary.");
        loaded.Source.ShouldBe(source);
    }

    [Fact]
    public async Task Sqlite_SourceId_SurvivesSaveAsync_UpsertPath()
    {
        // Guards the latent bug class the `kind` column hit: the UPSERT branch forgetting to bind
        // the parameter, silently blanking the value on every save.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var conv = ConversationFactory.CreateForWebhook(
            ConversationId.Create(), AgentId.From("agent-a"), sourceId: "wh-upsert");
        await store.CreateAsync(conv);

        conv.Title = "renamed";
        await store.SaveAsync(conv);

        var loaded = await fixture.CreateStore().GetAsync(conv.ConversationId);
        loaded!.SourceId.ShouldBe("wh-upsert",
            customMessage: "SaveAsync (upsert path) must preserve source_id. A null result here means " +
                "provenance is silently erased on every save.");
    }

    // ---------------------------------------------------------------------
    // Persistence: back-compat - THE critical test
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sqlite_LegacyRow_WithoutSourceIdColumn_LoadsAsNull()
    {
        // Seeds the schema as it existed BEFORE the `source_id` column, with a real row in it, then
        // opens the store normally so the additive migration runs. The legacy row must load with
        // SourceId = null and no error. This is the existing-user-data safety net.
        using var fixture = new StoreFixture();

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                CREATE TABLE conversations (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    purpose TEXT,
                    is_default INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL DEFAULT 'Active',
                    active_session_id TEXT,
                    metadata TEXT NOT NULL DEFAULT '{}',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    instructions TEXT,
                    canvas_html TEXT,
                    initiator TEXT,
                    kind TEXT NOT NULL DEFAULT 'HumanAgent',
                    source TEXT
                );
                CREATE TABLE conversation_bindings (
                    binding_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    channel_type TEXT NOT NULL,
                    channel_address TEXT NOT NULL,
                    mode TEXT NOT NULL DEFAULT 'Interactive',
                    threading_mode TEXT NOT NULL DEFAULT 'Single',
                    display_prefix TEXT,
                    bound_at TEXT NOT NULL,
                    last_inbound_at TEXT,
                    last_outbound_at TEXT
                );
                INSERT INTO conversations (id, agent_id, title, status, metadata, created_at, updated_at, kind, source)
                VALUES ('legacy-srcid', 'agent-a', 'before-source-id', 'Active', '{}', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z', 'HumanAgent', 'Cron');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var store = fixture.CreateStore();
        var loaded = await store.GetAsync(ConversationId.From("legacy-srcid"));

        loaded.ShouldNotBeNull();
        loaded!.Title.ShouldBe("before-source-id");
        loaded.Source.ShouldBe(ConversationSource.Cron);
        loaded.SourceId.ShouldBeNull(
            customMessage: "A row persisted before the `source_id` column existed must load with a null " +
                "SourceId. A throw here means shipping this breaks every existing install on first read.");

        // And the migrated row must save + re-read cleanly rather than tripping on the new column.
        await store.SaveAsync(loaded);
        var roundTrip = await fixture.CreateStore().GetAsync(ConversationId.From("legacy-srcid"));
        roundTrip!.SourceId.ShouldBeNull();
        roundTrip.Source.ShouldBe(ConversationSource.Cron);
    }

    [Fact]
    public void MapConversation_ProjectionWithoutSourceIdColumn_MapsToNull()
    {
        // A caller still selecting a pre-migration projection must not blow up on GetOrdinal.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                'conv-nosrcid' AS id,
                'agent-a' AS agent_id,
                'T' AS title,
                NULL AS purpose,
                0 AS is_default,
                'Active' AS status,
                NULL AS active_session_id,
                NULL AS metadata,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-01-02T03:04:05.0000000+00:00' AS updated_at,
                NULL AS instructions,
                NULL AS canvas_html,
                NULL AS initiator,
                NULL AS kind,
                'Cron' AS source,
                NULL AS world_id,
                0 AS is_pinned,
                NULL AS pinned_at,
                NULL AS todo_json,
                NULL AS pending_ask_user_json,
                NULL AS model_override,
                NULL AS thinking_override,
                NULL AS context_window_override
            """;

        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();

        var mapped = ConversationRowMapper.MapConversation(reader);
        mapped.SourceId.ShouldBeNull();
        mapped.Source.ShouldBe(ConversationSource.Cron);
    }

    [Theory]
    [InlineData("NULL", null)]
    [InlineData("'job-7'", "job-7")]
    public void MapConversation_SourceIdColumn_HydratesVerbatim_OrNull(string sqlLiteral, string? expected)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                'conv-srcid' AS id,
                'agent-a' AS agent_id,
                'T' AS title,
                NULL AS purpose,
                0 AS is_default,
                'Active' AS status,
                NULL AS active_session_id,
                NULL AS metadata,
                '2026-01-02T03:04:05.0000000+00:00' AS created_at,
                '2026-01-02T03:04:05.0000000+00:00' AS updated_at,
                NULL AS instructions,
                NULL AS canvas_html,
                NULL AS initiator,
                NULL AS kind,
                'Cron' AS source,
                {sqlLiteral} AS source_id,
                NULL AS world_id,
                0 AS is_pinned,
                NULL AS pinned_at,
                NULL AS todo_json,
                NULL AS pending_ask_user_json,
                NULL AS model_override,
                NULL AS thinking_override,
                NULL AS context_window_override
            """;

        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();

        ConversationRowMapper.MapConversation(reader).SourceId.ShouldBe(expected);
    }

    // ---------------------------------------------------------------------
    // Summary projection: the whole point of the clause
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sqlite_GetSummaries_CarriesSourceId()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var cron = ConversationFactory.CreateForCron(
            ConversationId.From("conv:cron-sum"), AgentId.From("agent-a"), title: "cron run", sourceId: "job-sum");
        var webhook = ConversationFactory.CreateForWebhook(
            ConversationId.From("conv:wh-sum"), AgentId.From("agent-a"), title: "wh run", sourceId: "wh-sum");
        var channel = ConversationFactory.CreateForChannel(
            ConversationId.From("conv:chan-sum"), AgentId.From("agent-a"), title: "chat");

        await store.CreateAsync(cron);
        await store.CreateAsync(webhook);
        await store.CreateAsync(channel);

        var summaries = await fixture.CreateStore().GetSummariesAsync();

        summaries.Single(s => s.ConversationId == "conv:cron-sum").SourceId.ShouldBe("job-sum",
            customMessage: "The summary must carry SourceId so the portal can classify a cron conversation " +
                "without a second per-feature list call (#2121).");
        summaries.Single(s => s.ConversationId == "conv:wh-sum").SourceId.ShouldBe("wh-sum");
        summaries.Single(s => s.ConversationId == "conv:chan-sum").SourceId.ShouldBeNull();
    }

    [Fact]
    public async Task InMemoryStore_GetSummaries_CarriesSourceId()
    {
        var store = new InMemoryConversationStore();
        var cron = ConversationFactory.CreateForCron(
            ConversationId.Create(), AgentId.From("agent-a"), title: "cron run", sourceId: "job-mem");
        await store.CreateAsync(cron);

        var summary = (await store.GetSummariesAsync())
            .Single(s => s.ConversationId == cron.ConversationId.Value);

        summary.SourceId.ShouldBe("job-mem");
        summary.Source.ShouldBe(ConversationSource.Cron.ToString());
    }

    [Fact]
    public void ConversationSummary_SourceId_DefaultsToNull()
    {
        // Back-compat for every existing construction site (and every deserialized payload from an
        // older gateway): the new field is optional and trailing.
        var summary = new ConversationSummary(
            "c1", "agent-a", "T", false, "Active", null, 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        summary.SourceId.ShouldBeNull();
        summary.Source.ShouldBe("Channel");
    }
}
