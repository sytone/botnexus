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
/// no-argument call, then asserts every persisted <c>role='tool'</c> RESULT row round-trips the
/// arguments that produced it.
/// </summary>
/// <remarks>
/// <para>
/// These tests deliberately drive the REAL streaming write path into the REAL
/// <see cref="SqliteSessionStore"/> and then read the raw <c>session_history</c> rows back with
/// SQL - the exact query the forensics scanner runs. Asserting on the in-memory
/// <c>SessionEntry</c> alone would not have caught the defect, because the loss #2906 reports is
/// only visible once the rows are on disk and read back without a self-join on
/// <c>tool_call_id</c>.
/// </para>
/// <para>
/// The measured production shape (2026-08 <c>sessions.db</c>) is a two-row pair per call: a start
/// row with args and a result row without. The pairing survives; what changes is that the result
/// row is now self-describing.
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

        // AC1: no result row for a tool invoked with arguments persists NULL.
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
        // The issue's headline query, run verbatim against a freshly recorded session:
        //   select count(*) from session_history where role='tool' and tool_args is null
        using var fixture = new StoreFixture();
        var session = await RecordSessionAsync(fixture);

        using var connection = new SqliteConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM session_history WHERE session_id = $id AND role = 'tool' AND tool_args IS NULL";
        command.Parameters.AddWithValue("$id", session.Value);

        Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
    }

    [Fact]
    public async Task RecordedSession_ResultRowContent_IsExtractedTextNotAToolContentToString()
    {
        // AC3: the result row must hold the tool's text, never the compiler-generated
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
        // The pairing invariant the forensics tooling relies on: a consumer reading ONLY the
        // result rows sees the same arguments a consumer reading the start rows sees, so the
        // self-join #2906 complains about becomes unnecessary rather than merely optional.
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
    /// Reads the persisted tool rows straight out of <c>session_history</c> with SQL, the way the
    /// forensics scanner does - not through the store's own mapper.
    /// </summary>
    private static List<ToolRow> ReadToolRows(StoreFixture fixture, SessionId sessionId)
    {
        using var connection = new SqliteConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tool_name, tool_call_id, tool_args, tool_is_error, content, message_kind
            FROM session_history
            WHERE session_id = $id AND role = 'tool'
            ORDER BY id
            """;
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
