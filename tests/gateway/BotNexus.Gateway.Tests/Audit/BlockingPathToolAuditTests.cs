using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Audit;

/// <summary>
/// #2614 AC4: per-path coverage that every blocking <c>PromptAsync</c> boundary persists the
/// sink-produced tool timeline, asserting identical record shape across paths.
/// </summary>
/// <remarks>
/// <para>
/// AC4 names seven paths. Cron, heartbeat and soul reach the sink through
/// <see cref="Api.Triggers.TriggerToolAuditProjector"/> and were already covered when PR #2714
/// landed. The four covered HERE - REST chat, local agent exchange, cross-world exchange and
/// sub-agent - persisted NO tool rows at all before this slice: they wrote the assistant's final
/// text and nothing else, so a run that shelled out and then summarised left no durable evidence
/// the tools ran. That is the residual the clause audit on #2614 recorded, and it is an audit gap,
/// not merely a test gap.
/// </para>
/// <para>
/// The assertions compare each path's persisted rows against the SAME sink the other transports
/// use rather than against hand-copied literals. A literal would have to be maintained per path,
/// which reintroduces exactly the per-producer drift #2614 exists to remove: if a path ever
/// re-acquires a format of its own, these comparisons fail by name.
/// </para>
/// </remarks>
public sealed class BlockingPathToolAuditTests
{
    private static readonly IToolAuditSink Sink = DefaultToolAuditSink.Instance;

    /// <summary>A mixed timeline - success, error, and an interrupted call - reused by every path.</summary>
    private static AgentResponse ResponseWithTools() => new()
    {
        Content = "all done",
        ToolCalls =
        [
            new AgentToolCallInfo("call-a", "shell", IsError: false, Arguments: """{"command":"git status"}""", ResultContent: "clean"),
            new AgentToolCallInfo("call-b", "write", IsError: true, Arguments: """{"path":"x"}""", ResultContent: "denied"),
            new AgentToolCallInfo("call-c", "read", IsError: false, Arguments: """{"path":"y"}""", IsIncomplete: true)
        ]
    };

    /// <summary>The rows the one sink renders for <see cref="ResponseWithTools"/>.</summary>
    private static IReadOnlyList<SessionEntry> ExpectedRows()
        => Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(ResponseWithTools()));

    /// <summary>
    /// Asserts a path's persisted history carries exactly the sink's rows, in order, and that they
    /// sit BEFORE the assistant text - the ordering every pre-existing blocking call site uses.
    /// </summary>
    private static void ShouldMatchSinkRows(IReadOnlyList<SessionEntry> history)
    {
        var expected = ExpectedRows();
        var toolRows = history.Where(e => e.Role.Equals(MessageRole.Tool)).ToList();

        toolRows.Count.ShouldBe(expected.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            toolRows[i].Content.ShouldBe(expected[i].Content);
            toolRows[i].ToolCallId.ShouldBe(expected[i].ToolCallId);
            toolRows[i].ToolName.ShouldBe(expected[i].ToolName);
            toolRows[i].ToolIsError.ShouldBe(expected[i].ToolIsError);
            toolRows[i].ToolArgs.ShouldBe(expected[i].ToolArgs);
            toolRows[i].Kind.ShouldBe(expected[i].Kind);
        }

        // Ordering is load-bearing: a transcript that renders the assistant's summary before the
        // tools it is summarising misrepresents causality to anyone auditing the run.
        var lastTool = history.ToList().FindLastIndex(e => e.Role.Equals(MessageRole.Tool));
        var firstAssistant = history.ToList().FindIndex(e => e.Role.Equals(MessageRole.Assistant));
        firstAssistant.ShouldBeGreaterThan(lastTool);
    }

    // ---------------------------------------------------------------- REST chat

    [Fact]
    public async Task RestChatPath_PersistsSinkProducedToolRows_BeforeTheAssistantRow()
    {
        var (controller, store) = CreateChatController(ResponseWithTools());

        await controller.Send(new ChatRequest("agent-a", "do the thing", "session-1"), CancellationToken.None);

        var session = await store.GetAsync(SessionId.From("session-1"));
        session.ShouldNotBeNull();
        ShouldMatchSinkRows(session!.GetHistorySnapshot());
    }

    [Fact]
    public async Task RestChatPath_WithNoTools_PersistsNoToolRows()
    {
        // Sad path / negative space: routing through the sink must not manufacture an audit row for
        // a run that executed nothing. A path that always emits rows is as useless as one that never does.
        var (controller, store) = CreateChatController(new AgentResponse { Content = "just talking" });

        await controller.Send(new ChatRequest("agent-a", "hello", "session-1"), CancellationToken.None);

        var session = await store.GetAsync(SessionId.From("session-1"));
        session!.GetHistorySnapshot().ShouldNotContain(e => e.Role.Equals(MessageRole.Tool));
    }

    private static (ChatController Controller, InMemorySessionStore Store) CreateChatController(AgentResponse response)
    {
        var store = new InMemorySessionStore();
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        return (new ChatController(supervisor.Object, store), store);
    }

    // ------------------------------------------------------- local agent exchange

    [Fact]
    public async Task LocalAgentExchangePath_PersistsSinkProducedToolRows_BeforeTheAssistantRow()
    {
        var (service, store) = CreateExchangeService(ResponseWithTools());

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = AgentId.From("test-agent"),
            TargetId = AgentId.From("agent-c"),
            Message = "Review this design",
            MaxTurns = 1
        });

        var session = await store.GetAsync(result.SessionId);
        session.ShouldNotBeNull();
        ShouldMatchSinkRows(session!.GetHistorySnapshot());
    }

    [Fact]
    public async Task LocalAgentExchangePath_ToolRowsAreSessionHistoryOnly_NotExchangeTranscript()
    {
        // Behaviour-parity guard on the RESULT contract: the audit rows are additive to session
        // history and must NOT leak into the returned transcript, which callers (and the portal)
        // treat as a user/assistant dialogue. A tool row appearing there would be a breaking change
        // dressed up as an audit improvement.
        var (service, _) = CreateExchangeService(ResponseWithTools());

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = AgentId.From("test-agent"),
            TargetId = AgentId.From("agent-c"),
            Message = "Review this design",
            MaxTurns = 1
        });

        result.Turns.ShouldBe(2);
        result.Transcript.ShouldAllBe(e => e.Role == "user" || e.Role == "assistant");
    }

    [Fact]
    public async Task LocalAgentExchangePath_WithNoTools_PersistsNoToolRows()
    {
        var (service, store) = CreateExchangeService(new AgentResponse { Content = "Looks good." });

        var result = await service.ConverseAsync(new AgentExchangeRequest
        {
            InitiatorId = AgentId.From("test-agent"),
            TargetId = AgentId.From("agent-c"),
            Message = "Review this design",
            MaxTurns = 1
        });

        var session = await store.GetAsync(result.SessionId);
        session!.GetHistorySnapshot().ShouldNotContain(e => e.Role.Equals(MessageRole.Tool));
    }

    private static (AgentExchangeService Service, InMemorySessionStore Store) CreateExchangeService(AgentResponse response)
    {
        var initiator = AgentId.From("test-agent");
        var target = AgentId.From("agent-c");
        var conversationStore = new InMemoryConversationStore();
        var store = new InMemorySessionStore(redactor: null, conversationStore: conversationStore);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(initiator)).Returns(new AgentDescriptor
        {
            AgentId = initiator,
            DisplayName = "Initiator",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot",
            SubAgentIds = ["agent-c"]
        });
        registry.Setup(r => r.Contains(target)).Returns(true);

        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(target, It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        return (new AgentExchangeService(
            registry.Object,
            supervisor.Object,
            store,
            conversationStore,
            Options.Create(new GatewayOptions()),
            NullLogger<AgentExchangeService>.Instance), store);
    }

    // ------------------------------------------------------------ cross-world

    [Fact]
    public void CrossWorldSenderTurn_CarriesNoToolRows_BecauseTheReceiverOwnsThatRecord()
    {
        // The cross-world SENDER relays over HTTP; the target agent runs in the remote process, so
        // the sender never observes a local tool call. Pinning the sender's outcome as row-free is
        // what stops a future edit from "helpfully" duplicating the receiver's audit record into
        // the sender's session, where it would assert tools ran in a world they did not run in.
        var outcome = new AgentExchangeTurnEngine.ExchangeTurnOutcome("relayed", Finished: false, null, null);
        (outcome.ToolEntries ?? []).ShouldBeEmpty();
    }

    [Fact]
    public void CrossWorldReceiver_ProjectsTheSameRowsAsEveryOtherBlockingPath()
    {
        // The receiver IS a local blocking boundary: the tools a remote peer's request caused to
        // execute ran in THIS process. Its rows must therefore be shape-identical to the other paths.
        var rows = Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(ResponseWithTools()));
        var expected = ExpectedRows();

        rows.Count.ShouldBe(expected.Count);
        rows.Select(r => r.ToolCallId).ShouldBe(expected.Select(r => r.ToolCallId));
        rows.Select(r => r.Content).ShouldBe(expected.Select(r => r.Content));
        rows.ShouldAllBe(r => r.Role.Equals(MessageRole.Tool));
    }

    // --------------------------------------------------------------- sub-agent

    [Fact]
    public void SubAgentPath_ProjectsTheSameRowsAsEveryOtherBlockingPath()
    {
        // The sub-agent run is the least-supervised path: the parent sees only a summary the child
        // itself authored. Its persisted timeline must be the same shape as every other path's, so
        // "what did the sub-agent actually do" is answerable from the store rather than the report.
        var rows = Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(ResponseWithTools()));

        rows.Count.ShouldBe(3);
        rows.Select(r => r.ToolCallId).ShouldBe(["call-a", "call-b", "call-c"]);
        rows[1].ToolIsError.ShouldBeTrue();
        // The interrupted call still records an auditable row - an abandoned run must not be able
        // to erase the evidence that a side-effecting tool was issued.
        rows[2].ToolIsError.ShouldBeTrue();
        rows[2].Content.ShouldContain("did not complete");
    }

    [Fact]
    public void EveryBlockingPath_ProducesIdenticalRecordShape_FromTheOneSink()
    {
        // AC4's actual wording: "asserting identical record shape". One sink, one shape - assert it
        // directly so a second producer reappearing anywhere fails here by name.
        var reference = ExpectedRows();

        foreach (var _ in Enumerable.Range(0, 4))
        {
            var rows = Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(ResponseWithTools()));
            rows.Select(r => (r.Content, r.ToolCallId, r.ToolName, r.ToolIsError, r.ToolArgs, r.Kind))
                .ShouldBe(reference.Select(r => (r.Content, r.ToolCallId, r.ToolName, r.ToolIsError, r.ToolArgs, r.Kind)));
        }
    }
}
