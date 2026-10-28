using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Audit;

/// <summary>
/// Issue #2906 AC4: records a session containing a <c>shell</c> call, an <c>edit</c> call and a
/// no-argument call, then asserts every durable <c>role='tool'</c> result projection round-trips
/// the arguments that produced it through its normalized evidence link.
/// </summary>
/// <remarks>
/// <para>
/// These tests deliberately drive the REAL streaming write path into the REAL
/// <see cref="SqliteSessionStore"/> and then reconstruct <c>session_history</c> projections with
/// a SQL join to <c>tool_invocations</c> on the persisted invocation link and session identity.
/// Asserting on the in-memory <c>SessionEntry</c> alone would not catch lost durable evidence.
/// The join restores start arguments and result text only; result arguments remain per-row
/// evidence and must not be invented from the normalized invocation when absent.
/// </para>
/// <para>
/// The start/result pair survives the normalized write cutover. Raw start arguments and raw
/// result content are intentionally NULL; their payloads live in the linked invocation. Separate
/// raw-storage assertions verify both the narrow markers and the intact normalized evidence.
/// </para>
/// </remarks>
public sealed class ToolResultRowArgumentsPersistenceTests
{
    private const string ShellArgs = """{"command":"git status --short"}""";
    private const string EditArgs = """{"path":"src/a.cs","oldText":"a","newText":"b"}""";

    [Fact]
    public async Task RecordedSession_EveryToolResultRow_RoundTripsItsArguments()
    {
        using var fixture = new StoreFixture();
        var session = await RecordSessionAsync(fixture);

        var rows = ReadToolRows(fixture, session);

        // Three calls, each producing the start/result pair the production schema uses.
        rows.Count.ShouldBe(6);

        var results = rows.Where(r => r.Kind == "tool-result").ToList();
        results.Count.ShouldBe(3);

        // AC1: no durable result projection for a tool invoked with arguments has NULL args.
        results.Single(r => r.ToolName == "shell").ToolArgs.ShouldBe(ShellArgs);
        results.Single(r => r.ToolName == "edit").ToolArgs.ShouldBe(EditArgs);

        // AC2: the no-arg tool records the empty object, NOT null, so "no args" stays
        // distinguishable from "args lost".
        results.Single(r => r.ToolName == "datetime_helper").ToolArgs.ShouldBe("{}");
        results.ShouldAllBe(r => r.ToolArgs != null);
    }

    [Fact]
    public async Task RecordedSession_HasNoToolRowWithNullArguments()
    {
        // The issue's no-lost-arguments invariant applies to the durable reconstructed
        // projection, not the intentionally narrow raw start markers.
        using var fixture = new StoreFixture();
        var session = await RecordSessionAsync(fixture);

        using var connection = new SqliteConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) FROM ({ToolRowProjectionSql}) WHERE tool_args IS NULL
            """;
        command.Parameters.AddWithValue("$id", session.Value);

        Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
    }

    [Fact]
    public async Task RecordedSession_ResultRowContent_IsExtractedTextNotAToolContentToString()
    {
        // AC3: the durable result projection must hold the tool's text, never the compiler-generated
        // "AgentToolContent { Type = Text, Value = ... }" record ToString that #2906 observed in
        // 13k production rows.
        using var fixture = new StoreFixture();
        var session = await RecordSessionAsync(fixture);

        var results = ReadToolRows(fixture, session).Where(r => r.Kind == "tool-result").ToList();

        results.Single(r => r.ToolName == "shell").Content.ShouldBe(" M src/a.cs");
        results.ShouldAllBe(r => !r.Content.StartsWith("AgentToolContent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordedSession_StartAndResultRows_ShareCallIdAndAgreeOnArguments()
    {
        // The pairing invariant the forensics tooling relies on: reconstructed result rows
        // expose the same arguments as reconstructed start rows, without a history self-join
        // on tool_call_id. Each marker instead links to its normalized durable evidence.
        using var fixture = new StoreFixture();
        var session = await RecordSessionAsync(fixture);

        var byCall = ReadToolRows(fixture, session)
            .GroupBy(r => r.ToolCallId)
            .ToList();

        byCall.Count.ShouldBe(3);
        foreach (var pair in byCall)
        {
            pair.Count().ShouldBe(2);
            var start = pair.Single(r => r.Kind == "tool-start");
            var result = pair.Single(r => r.Kind == "tool-result");
            result.ToolArgs.ShouldBe(start.ToolArgs);
        }
    }

    [Fact]
    public async Task InterruptedCall_SynthesizedResultRow_StillCarriesItsArguments()
    {
        // An interrupted call is the case forensics most needs the inputs for; the #1001 orphan
        // synthesis must not be the one path that still loses them.
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var sessionId = SessionId.From("s-2906-orphan");
        var session = await store.GetOrCreateAsync(sessionId, AgentId.From("agent-2906"));

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolStart,
                    ToolCallId = "call-orphan",
                    ToolName = "shell",
                    ToolArgs = new Dictionary<string, object?> { ["command"] = "git status --short" }
                }
            ]),
            session,
            store);

        var rows = ReadToolRows(fixture, sessionId);
        var synthesized = rows.Single(r => r.Kind == "tool-result");
        synthesized.ToolArgs.ShouldNotBeNull();
        synthesized.ToolArgs.ShouldContain("git status --short");
        synthesized.ToolIsError.ShouldBeTrue();
    }

    [Fact]
    public async Task RecordedSession_RawMarkersAreNarrowAndLinkedNormalizedPayloadsAreIntact()
    {
        using var fixture = new StoreFixture();
        var session = await RecordSessionAsync(fixture);
        var expected = new Dictionary<string, (string Name, string Args, string Content)>
        {
            ["call-shell"] = ("shell", ShellArgs, " M src/a.cs"),
            ["call-edit"] = ("edit", EditArgs, "Replaced 1 occurrence."),
            ["call-noargs"] = ("datetime_helper", "{}", "2026-08-11T01:00:00Z")
        };

        using var connection = new SqliteConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.message_kind, h.tool_args, h.content, h.tool_invocation_id,
                   i.id, i.arguments_json, i.result_content, i.tool_call_id, h.tool_call_id,
                   i.tool_name, h.tool_name
            FROM session_history h
            LEFT JOIN tool_invocations i ON i.id = h.tool_invocation_id AND i.session_id = h.session_id
            WHERE h.session_id = $id AND h.role = 'tool'
            ORDER BY h.id
            """;
        command.Parameters.AddWithValue("$id", session.Value);

        var starts = new List<string>();
        var results = new List<string>();
        var invocationIds = new HashSet<long>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                reader.IsDBNull(3).ShouldBeFalse();
                reader.IsDBNull(4).ShouldBeFalse();
                reader.GetInt64(3).ShouldBe(reader.GetInt64(4));
                invocationIds.Add(reader.GetInt64(4));
                var callId = reader.GetString(8);
                reader.GetString(7).ShouldBe(callId);
                var payload = expected[callId];
                reader.GetString(9).ShouldBe(payload.Name);
                reader.GetString(10).ShouldBe(payload.Name);
                reader.GetString(5).ShouldBe(payload.Args);
                reader.GetString(6).ShouldBe(payload.Content);

                var kind = reader.GetString(0);
                if (kind == "tool-start")
                {
                    reader.IsDBNull(1).ShouldBeTrue();
                    starts.Add(callId);
                }
                else
                {
                    kind.ShouldBe("tool-result");
                    reader.IsDBNull(2).ShouldBeTrue();
                    // Result arguments remain per-row evidence, not a normalized fallback.
                    reader.GetString(1).ShouldBe(payload.Args);
                    results.Add(callId);
                }
            }
        }

        // Exactly three narrow starts and three narrow results, with one linked pair per call.
        starts.Order().ShouldBe(expected.Keys.Order());
        results.Order().ShouldBe(expected.Keys.Order());
        starts.Count.ShouldBe(3);
        results.Count.ShouldBe(3);
        invocationIds.Count.ShouldBe(3);
        command.CommandText = "SELECT COUNT(*) FROM tool_invocations WHERE session_id = $id";
        Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(3);
    }

    /// <summary>
    /// Drives one streamed agent run containing a shell call, an edit call, and a no-argument call
    /// through the production streaming writer and the production SQLite store.
    /// </summary>
    private static async Task<SessionId> RecordSessionAsync(StoreFixture fixture)
    {
        var store = fixture.CreateStore();
        var sessionId = SessionId.From("s-2906");
        var session = await store.GetOrCreateAsync(sessionId, AgentId.From("agent-2906"));

        await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(
            [
                new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, MessageId = "m1" },
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolStart,
                    ToolCallId = "call-shell",
                    ToolName = "shell",
                    ToolArgs = new Dictionary<string, object?> { ["command"] = "git status --short" }
                },
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolEnd,
                    ToolCallId = "call-shell",
                    ToolName = "shell",
                    ToolResult = " M src/a.cs"
                },
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolStart,
                    ToolCallId = "call-edit",
                    ToolName = "edit",
                    ToolArgs = new Dictionary<string, object?>
                    {
                        ["path"] = "src/a.cs",
                        ["oldText"] = "a",
                        ["newText"] = "b"
                    }
                },
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolEnd,
                    ToolCallId = "call-edit",
                    ToolName = "edit",
                    ToolResult = "Replaced 1 occurrence."
                },
                // The no-argument tool: ToolArgs is genuinely absent, not merely unobserved.
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolStart,
                    ToolCallId = "call-noargs",
                    ToolName = "datetime_helper"
                },
                new AgentStreamEvent
                {
                    Type = AgentStreamEventType.ToolEnd,
                    ToolCallId = "call-noargs",
                    ToolName = "datetime_helper",
                    ToolResult = "2026-08-11T01:00:00Z"
                },
                new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "done", MessageId = "m1" },
                new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, MessageId = "m1" }
            ]),
            session,
            store);

        return sessionId;
    }

    /// <summary>
    /// Reconstructs persisted tool rows with a session-scoped normalized evidence join, using
    /// the production projection contract but independent SQL rather than the store's mapper.
    /// </summary>
    private static List<ToolRow> ReadToolRows(StoreFixture fixture, SessionId sessionId)
    {
        using var connection = new SqliteConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = ToolRowProjectionSql;
        command.Parameters.AddWithValue("$id", sessionId.Value);

        var rows = new List<ToolRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ToolRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    // Match the durable projection: only start args and result text have normalized fallbacks.
    private const string ToolRowProjectionSql = """
        SELECT h.tool_name, h.tool_call_id,
               CASE WHEN h.tool_args IS NOT NULL THEN h.tool_args
                    WHEN h.message_kind = 'tool-start' THEN i.arguments_json END AS tool_args,
               h.tool_is_error,
               COALESCE(h.content, CASE WHEN h.message_kind = 'tool-result' THEN i.result_content END) AS content,
               h.message_kind
        FROM session_history h
        LEFT JOIN tool_invocations i ON i.id = h.tool_invocation_id AND i.session_id = h.session_id
        WHERE h.session_id = $id AND h.role = 'tool'
        ORDER BY h.id
        """;

    private sealed record ToolRow(
        string? ToolName,
        string? ToolCallId,
        string? ToolArgs,
        bool ToolIsError,
        string Content,
        string? Kind);

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(IEnumerable<AgentStreamEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class StoreFixture : IDisposable
    {
        public StoreFixture()
        {
            DirectoryPath = Path.Combine(
                AppContext.BaseDirectory,
                "ToolResultRowArgumentsPersistenceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "sessions.db");
            ConnectionString = $"Data Source={DatabasePath};Pooling=False";
            Conversations = new InMemoryConversationStore();
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string ConnectionString { get; }
        public InMemoryConversationStore Conversations { get; }

        public SqliteSessionStore CreateStore(IConversationStore? conversationStore = null)
            => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, conversationStore ?? Conversations);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
