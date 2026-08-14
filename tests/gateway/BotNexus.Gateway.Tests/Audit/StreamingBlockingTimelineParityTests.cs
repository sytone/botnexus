using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;

namespace BotNexus.Gateway.Tests.Audit;

/// <summary>
/// #2616 AC3: the portal/history API must return the SAME tool timeline shape for a streaming and
/// a blocking execution of the same scripted tool sequence.
/// </summary>
/// <remarks>
/// <para>
/// AC1/AC2 are enforced structurally by the architecture fence, which proves every execution call
/// site <i>reaches</i> the sink. That is necessary but not sufficient for the property an operator
/// actually depends on: that what they SEE in the portal does not depend on which transport served
/// the turn. A blocking turn that reached the sink but persisted rows the history assembler then
/// filtered, reordered or projected differently would satisfy the fence and still leave the two
/// transports rendering different histories.
/// </para>
/// <para>
/// These tests therefore run the same scripted tool sequence through both real persistence paths -
/// <see cref="StreamingSessionHelper.ProcessAndSaveAsync"/> for streaming, and the blocking sink
/// projection the <c>PromptAsync</c> boundaries use - and compare the assembled
/// <see cref="ConversationHistoryEntry"/> timelines the portal consumes. The comparison is against
/// each other, never against a hand-copied literal: a literal would need maintaining on both sides,
/// which is the drift being fenced.
/// </para>
/// <para>
/// The streamed timeline legitimately carries an extra <c>ToolStart</c> row per call, because the
/// streaming boundary observes a start it cannot know the result of yet, whereas a blocking run is
/// only ever handed a settled timeline. The invariant is therefore stated over the RESULT rows -
/// one per call, same order, same identity, same arguments, same error flag - which is the shape a
/// portal timeline and an audit query both read.
/// </para>
/// </remarks>
public sealed class StreamingBlockingTimelineParityTests
{
    private static readonly IToolAuditSink Sink = DefaultToolAuditSink.Instance;

    /// <summary>The scripted sequence: a success, a failure and an interrupted call.</summary>
    private static readonly (string Id, string Name, string Args, string? Result, bool IsError, bool Incomplete)[] Script =
    [
        ("call-a", "shell", """{"command":"git status"}""", "clean", false, false),
        ("call-b", "write", """{"path":"x"}""", "denied", true, false),
        ("call-c", "read", """{"path":"y"}""", null, false, true)
    ];

    [Fact]
    public async Task HistoryApi_RendersTheSameToolTimeline_ForStreamingAndBlockingRuns()
    {
        var streamed = await AssembleStreamedAsync();
        var blocking = await AssembleBlockingAsync();

        var streamedResults = ToolResultRows(streamed);
        var blockingResults = ToolResultRows(blocking);

        // Non-vacuity: a comparison of two empty timelines would pass trivially.
        streamedResults.Count.ShouldBe(Script.Length,
            "the streamed run must record one result row per scripted tool call");
        blockingResults.Count.ShouldBe(Script.Length,
            "the blocking run must record one result row per scripted tool call");

        for (var i = 0; i < streamedResults.Count; i++)
        {
            var s = streamedResults[i];
            var b = blockingResults[i];

            b.ToolCallId.ShouldBe(s.ToolCallId, $"tool-call identity diverged at position {i}");
            b.ToolName.ShouldBe(s.ToolName, $"tool name diverged at position {i}");
            b.ToolArgs.ShouldBe(s.ToolArgs, $"tool arguments diverged at position {i}");
            b.ToolIsError.ShouldBe(s.ToolIsError, $"error flag diverged at position {i}");
            b.Content.ShouldBe(s.Content, $"rendered content diverged at position {i}");
            b.Role.ShouldBe(s.Role, $"role diverged at position {i}");
            b.MessageKind.ShouldBe(s.MessageKind, $"typed message kind diverged at position {i}");
        }

        // Execution order is part of the shape: an audit read that reorders calls misreports what
        // happened, even with every individual row correct.
        blockingResults.Select(r => r.ToolCallId).ShouldBe(Script.Select(s => s.Id).ToList());
    }

    [Fact]
    public async Task BothTransports_SurviveTheHistoryAssemblersRowFilters()
    {
        // The assembler drops NO_REPLY rows and #2921 contentless assistant ghosts. A tool row must
        // not be collateral of either filter on EITHER transport - a filtered audit row is an audit
        // row that does not exist as far as the portal and any history-based review is concerned.
        var streamed = await AssembleStreamedAsync();
        var blocking = await AssembleBlockingAsync();

        ToolResultRows(streamed).ShouldNotBeEmpty("streamed tool rows must survive history assembly");
        ToolResultRows(blocking).ShouldNotBeEmpty("blocking tool rows must survive history assembly");

        // Every surviving tool row is attributable: an unnamed, uncorrelated row is not evidence.
        foreach (var row in ToolResultRows(streamed).Concat(ToolResultRows(blocking)))
        {
            row.ToolName.ShouldNotBeNullOrWhiteSpace();
            row.ToolCallId.ShouldNotBeNullOrWhiteSpace();
            row.ToolArgs.ShouldNotBeNull("#2906: no audit row may persist NULL arguments");
        }
    }

    /// <summary>Persists the scripted run through the real streaming helper and assembles history.</summary>
    private static async Task<IReadOnlyList<ConversationHistoryEntry>> AssembleStreamedAsync()
    {
        var conversationId = ConversationId.From("c_stream");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-stream"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;
        await sessions.SaveAsync(session);

        await StreamingSessionHelper.ProcessAndSaveAsync(ScriptedStream(), session, sessions);

        return await AssembleAsync(conversationId, sessions);
    }

    /// <summary>Persists the scripted run through the blocking sink projection and assembles history.</summary>
    private static async Task<IReadOnlyList<ConversationHistoryEntry>> AssembleBlockingAsync()
    {
        var conversationId = ConversationId.From("c_blocking");
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("s-blocking"), AgentId.From("quill"));
        session.Session.ConversationId = conversationId;

        var response = new AgentResponse
        {
            Content = "all done",
            ToolCalls = Script
                .Select(s => new AgentToolCallInfo(s.Id, s.Name, s.IsError, Arguments: s.Args, ResultContent: s.Result, IsIncomplete: s.Incomplete))
                .ToList()
        };

        foreach (var row in Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(response)))
            session.AddEntry(row);
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = response.Content });
        await sessions.SaveAsync(session);

        return await AssembleAsync(conversationId, sessions);
    }

    /// <summary>Emits the scripted sequence as the stream events the streaming boundary observes.</summary>
    private static async IAsyncEnumerable<AgentStreamEvent> ScriptedStream()
    {
        foreach (var (id, name, args, result, isError, incomplete) in Script)
        {
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.ToolStart,
                ToolCallId = id,
                ToolName = name,
                ToolArgs = ArgsOf(name)
            };

            if (incomplete)
                continue;

            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.ToolEnd,
                ToolCallId = id,
                ToolName = name,
                ToolArgs = ArgsOf(name),
                ToolResult = result,
                ToolIsError = isError
            };
        }

        yield return new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "all done" };
        yield return new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd };
        await Task.CompletedTask;
    }

    /// <summary>
    /// The streamed boundary carries arguments as a dictionary; the blocking boundary carries the
    /// already-serialized string. Both are normalised by the one sink, which is exactly the parity
    /// under test - so the dictionary here must serialize to the same JSON the script states.
    /// </summary>
    /// <param name="toolName">The scripted tool whose arguments to build.</param>
    /// <returns>The argument dictionary for that call.</returns>
    private static IReadOnlyDictionary<string, object?> ArgsOf(string toolName) => toolName switch
    {
        "shell" => new Dictionary<string, object?> { ["command"] = "git status" },
        "write" => new Dictionary<string, object?> { ["path"] = "x" },
        _ => new Dictionary<string, object?> { ["path"] = "y" }
    };

    private static async Task<IReadOnlyList<ConversationHistoryEntry>> AssembleAsync(
        ConversationId conversationId,
        InMemorySessionStore sessions)
    {
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("quill"),
            Title = "Default",
            IsDefault = true,
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var assembler = new ConversationHistoryAssembler(conversations, sessions);
        var result = await assembler.AssembleAsync(conversationId, limit: 500, offset: 0);
        result.ShouldNotBeNull();
        return result!.Entries;
    }

    /// <summary>
    /// The tool RESULT rows of an assembled timeline, in order. Start rows are excluded because
    /// only the streaming boundary can observe an unsettled call; see the class remarks.
    /// </summary>
    private static List<ConversationHistoryEntry> ToolResultRows(IReadOnlyList<ConversationHistoryEntry> entries)
        => entries
            .Where(e => string.Equals(e.Role, "tool", StringComparison.Ordinal))
            .Where(e => !string.Equals(e.MessageKind, MessageKind.ToolStart.Value, StringComparison.Ordinal))
            .ToList();
}
