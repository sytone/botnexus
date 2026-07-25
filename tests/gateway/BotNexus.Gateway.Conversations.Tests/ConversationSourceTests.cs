using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Slice A of epic #2300 (issue #2301) — pins the <see cref="ConversationSource"/> contract at the
/// two layers it lands in: the domain aggregate (write-once provenance) and SQLite persistence
/// (round-trip + migration back-compat).
/// <para>
/// The critical test here is <see cref="Sqlite_LegacyRow_WithoutSourceColumn_LoadsAsChannel"/>:
/// every conversation row that exists in a user's database today predates this column, so the
/// additive migration plus the <c>Channel = 0</c> default must hydrate those rows silently. A
/// failure there means shipping this breaks existing installs on first read.
/// </para>
/// </summary>
public sealed class ConversationSourceTests
{
    private static Conversation NewConversation(string title, ConversationSource source)
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From("agent-a"),
            Title = title,
            Status = ConversationStatus.Active,
            Source = source,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    // ---------------------------------------------------------------------
    // Domain: enum shape + write-once semantics
    // ---------------------------------------------------------------------

    [Fact]
    public void ConversationSource_Channel_IsDefaultValue()
    {
        // The whole back-compat story rests on Channel being the CLR default: a `default`
        // Conversation, a missing column and an unparseable value must all land on Channel.
        // This mirrors the ConversationKind.HumanAgent = 0 precedent.
        ((int)ConversationSource.Channel).ShouldBe(0);
        default(ConversationSource).ShouldBe(ConversationSource.Channel);
    }

    [Fact]
    public void ConversationSource_HasExactlyFourValues_CoarseByDesign()
    {
        // Deliberately coarse (#2301): Agent covers both sub-agent supervision and peer converse
        // because ConversationKind already disambiguates those. A fifth value would re-introduce
        // overlap between the two axes — if this fails, re-read the issue before "fixing" it.
        Enum.GetValues<ConversationSource>().ShouldBe(
        [
            ConversationSource.Channel,
            ConversationSource.Cron,
            ConversationSource.Webhook,
            ConversationSource.Agent
        ]);
    }

    [Fact]
    public void Conversation_Source_DefaultsToChannel_WhenNotStamped()
    {
        // No call site is required to change in slice A: anything that doesn't stamp gets Channel.
        var conv = new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From("agent-a"),
            Title = "unstamped"
        };

        conv.Source.ShouldBe(ConversationSource.Channel);
    }

    [Fact]
    public void Conversation_Source_IsWriteOnce_NoPublicSetter()
    {
        // Write-once is enforced by the compiler (init-only), so the guard has to be reflective:
        // a plain `set` accessor is distinguishable from `init` by the IsExternalInit modreq on
        // the setter's return type. If someone converts this to `{ get; set; }` to make an
        // assignment compile, this test fails loudly — that is the point.
        var property = typeof(Conversation).GetProperty(nameof(Conversation.Source));
        property.ShouldNotBeNull();

        var setter = property!.SetMethod;
        setter.ShouldNotBeNull(
            customMessage: "Source must remain settable at construction time via an object initializer.");

        setter!.ReturnParameter.GetRequiredCustomModifiers()
            .ShouldContain(typeof(System.Runtime.CompilerServices.IsExternalInit),
                customMessage: "Conversation.Source must be init-only (write-once provenance, #2301). " +
                    "A public setter would let an inbound event re-stamp origination after the fact — " +
                    "exactly the mutable-flag failure mode epic #2300 exists to remove.");
    }

    // ---------------------------------------------------------------------
    // Persistence: round-trip every value
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(ConversationSource.Channel)]
    [InlineData(ConversationSource.Cron)]
    [InlineData(ConversationSource.Webhook)]
    [InlineData(ConversationSource.Agent)]
    public async Task Sqlite_Source_RoundTrips_AllValues_AcrossProcessRestart(ConversationSource source)
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var conv = NewConversation($"roundtrip-{source}", source);
        await store.CreateAsync(conv);

        // Fresh store instance — proves the value survives at the SQL level rather than being
        // retained by the in-memory clone cache.
        var fresh = fixture.CreateStore();
        var loaded = await fresh.GetAsync(conv.ConversationId);

        loaded.ShouldNotBeNull();
        loaded!.Source.ShouldBe(source,
            customMessage: "Source must round-trip through the SQLite schema. If this returns Channel, the " +
                "INSERT or SELECT is missing the `source` binding and the field is dropping at the SQL boundary.");
    }

    [Theory]
    [InlineData(ConversationSource.Cron)]
    [InlineData(ConversationSource.Webhook)]
    [InlineData(ConversationSource.Agent)]
    public async Task Sqlite_Source_SurvivesSaveAsync_UpsertPath(ConversationSource source)
    {
        // Guards the exact latent bug P9-A hit with `kind`: the UPSERT branch forgetting to bind
        // the parameter, silently demoting the value to the default on every save.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var conv = NewConversation($"upsert-{source}", source);
        await store.CreateAsync(conv);

        conv.Title = $"upsert-{source}-updated";
        await store.SaveAsync(conv);

        var fresh = fixture.CreateStore();
        var loaded = await fresh.GetAsync(conv.ConversationId);
        loaded!.Source.ShouldBe(source,
            customMessage: "SaveAsync (upsert path) must bind $source. A Channel result here means Source is " +
                "silently demoted on every save.");
    }

    // ---------------------------------------------------------------------
    // Persistence: back-compat — THE critical test
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sqlite_LegacyRow_WithoutSourceColumn_LoadsAsChannel()
    {
        // Seeds the schema as it existed BEFORE the `source` column, with a real row in it, then
        // opens the store normally so the additive migration runs. The legacy row must load with
        // Source = Channel and no error. This is the existing-user-data safety net.
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
                    kind TEXT NOT NULL DEFAULT 'HumanAgent'
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

                INSERT INTO conversations (id, agent_id, title, status, metadata, created_at, updated_at, kind)
                VALUES ('legacy-src', 'agent-a', 'before-source', 'Active', '{}', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z', 'HumanAgent');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var store = fixture.CreateStore();
        var loaded = await store.GetAsync(ConversationId.From("legacy-src"));

        loaded.ShouldNotBeNull();
        loaded!.Title.ShouldBe("before-source");
        loaded.Source.ShouldBe(ConversationSource.Channel,
            customMessage: "A row persisted before the `source` column existed must load as Channel. " +
                "A throw or a non-Channel value here means shipping this breaks every existing install.");

        // And the migrated row must save + re-read cleanly rather than tripping on the new column.
        await store.SaveAsync(loaded);
        var fresh = fixture.CreateStore();
        var roundTrip = await fresh.GetAsync(ConversationId.From("legacy-src"));
        roundTrip!.Source.ShouldBe(ConversationSource.Channel);
    }

    // ---------------------------------------------------------------------
    // Mapper-level: NULL and malformed values degrade to Channel, never throw
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("NULL", ConversationSource.Channel)]
    [InlineData("'Cron'", ConversationSource.Cron)]
    [InlineData("'webhook'", ConversationSource.Webhook)]     // case-insensitive parse
    [InlineData("'not-a-source'", ConversationSource.Channel)] // garbage degrades, does not throw
    public void MapConversation_SourceColumn_DegradesToChannel_OnNullOrGarbage(string sqlLiteral, ConversationSource expected)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                'conv-src' AS id,
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
                {sqlLiteral} AS source,
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

        ConversationRowMapper.MapConversation(reader).Source.ShouldBe(expected);
    }

    [Fact]
    public void MapConversation_ProjectionWithoutSourceColumn_MapsToChannel()
    {
        // A caller still selecting a pre-migration projection must not blow up on GetOrdinal.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                'conv-nosrc' AS id,
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

        ConversationRowMapper.MapConversation(reader).Source.ShouldBe(ConversationSource.Channel);
    }
}
