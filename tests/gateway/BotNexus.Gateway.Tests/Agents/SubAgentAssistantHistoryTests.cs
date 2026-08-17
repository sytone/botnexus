using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Issue #3256: a sub-agent session must persist <see cref="MessageRole.Assistant"/> rows, not only
/// <see cref="MessageRole.Tool"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// The defect was total and silent: 533 of 533 sub-agent sessions in the live store contained
/// exactly one role. The child conversation is flagged <c>UserFacing</c>, so it opened and rendered
/// a wall of tool invocations with no model output between them - indistinguishable from a broken
/// renderer. The run's summary existed only as a transient completion event delivered to the
/// parent, and was unrecoverable afterwards.
/// </para>
/// <para>
/// These tests assert the persisted ROLE SET rather than the exact text, because the acceptance
/// criterion is parity with a normal blocking session, not a particular phrasing. The parity case
/// compares the sub-agent's role set against the role set the shared blocking contract produces, so
/// a future path that drops the assistant append reddens by name.
/// </para>
/// </remarks>
public sealed class SubAgentAssistantHistoryTests
{
    private const string ParentSession = "parent-session";

    [Fact]
    public async Task CompletedRun_PersistsAssistantRow_CarryingTheFinalSummary()
    {
        var (manager, store) = CreateManager(new AgentResponse { Content = "I read three files and found the leak." });

        var info = await manager.SpawnAsync(BuildRequest());
        await WaitForCompletionAsync(manager, info.SubAgentId);

        var history = await ChildHistoryAsync(store, info.ChildSessionId);
        var assistantRows = history.Where(e => e.Role.Equals(MessageRole.Assistant)).ToList();

        assistantRows.ShouldNotBeEmpty();
        assistantRows[^1].Content.ShouldContain("found the leak");
    }

    [Fact]
    public async Task CompletedRun_PersistedSummary_IsReadableFromTheStoreAfterTheRunEnds()
    {
        // AC2: durability is the point. The completion event has already been dispatched and
        // discarded by this line; the only surviving copy must be the one in the session store.
        var (manager, store) = CreateManager(new AgentResponse { Content = "durable summary text" });

        var info = await manager.SpawnAsync(BuildRequest());
        await WaitForCompletionAsync(manager, info.SubAgentId);

        var history = await ChildHistoryAsync(store, info.ChildSessionId);
        history.ShouldContain(e => e.Role.Equals(MessageRole.Assistant) && e.Content == "durable summary text");
    }

    [Fact]
    public async Task CompletedRun_OrdersToolRowsBeforeTheAssistantSummary()
    {
        // AC3: model output interleaved with tool calls IN ORDER. A transcript that renders the
        // summary before the tools it is summarising misrepresents causality to an auditor.
        var (manager, store) = CreateManager(new AgentResponse
        {
            Content = "done",
            ToolCalls =
            [
                new AgentToolCallInfo("call-a", "read", IsError: false, Arguments: """{"path":"x"}""", ResultContent: "ok")
            ]
        });

        var info = await manager.SpawnAsync(BuildRequest());
        await WaitForCompletionAsync(manager, info.SubAgentId);

        var history = (await ChildHistoryAsync(store, info.ChildSessionId)).ToList();
        var lastTool = history.FindLastIndex(e => e.Role.Equals(MessageRole.Tool));
        var firstAssistant = history.FindIndex(e => e.Role.Equals(MessageRole.Assistant));

        lastTool.ShouldBeGreaterThanOrEqualTo(0);
        firstAssistant.ShouldBeGreaterThan(lastTool);
    }

    [Fact]
    public async Task FailedRun_PersistsItsDiagnostic_AsAnAssistantRow()
    {
        // Sad path: a run that produced nothing still has a terminal disposition worth reading.
        // Persisting the diagnostic is what keeps a failed delegated run auditable at all.
        var (manager, store) = CreateManager(new AgentResponse { Content = string.Empty });

        var info = await manager.SpawnAsync(BuildRequest());
        await WaitForCompletionAsync(manager, info.SubAgentId);

        var history = await ChildHistoryAsync(store, info.ChildSessionId);
        history.ShouldContain(e =>
            e.Role.Equals(MessageRole.Assistant) && e.Content.Contains("empty final response"));
    }

    [Fact]
    public async Task Run_WithoutSessionStore_StillCompletes_AndPersistsNothing()
    {
        // Sad path: persistence is best-effort. A host with no ISessionStore must not change the
        // run's terminal disposition - the append is additive, never load-bearing.
        var manager = CreateManagerWithoutStore(new AgentResponse { Content = "fine" });

        var info = await manager.SpawnAsync(BuildRequest());
        var final = await WaitForCompletionAsync(manager, info.SubAgentId);

        final.Status.ShouldBe(SubAgentStatus.Completed);
        final.ResultSummary.ShouldBe("fine");
    }

    [Fact]
    public async Task Run_WhenTheStoreThrowsOnSave_StillCompletesSuccessfully()
    {
        // Sad path: a store fault on the SUMMARY write must not turn a successful delegated run
        // into a failure. The fault is scoped to writes after the first because the spawn-time bind
        // in MintChildConversationAsync is deliberately NOT best-effort - failing every save would
        // assert against that pre-existing (and separate) contract instead of against this fix.
        var saves = 0;
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession());
        sessionStore.Setup(s => s.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySession());
        sessionStore.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref saves) == 1
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("store down")));

        var manager = BuildManager(sessionStore.Object, new AgentResponse { Content = "still fine" });

        var info = await manager.SpawnAsync(BuildRequest());
        var final = await WaitForCompletionAsync(manager, info.SubAgentId);

        final.Status.ShouldBe(SubAgentStatus.Completed);
        final.ResultSummary.ShouldBe("still fine");
    }

    [Fact]
    public async Task SubAgentSession_RoleSet_HasParityWithANormalBlockingSession()
    {
        // AC4 / AC5: the parity clause. A normal blocking turn persists user + tool + assistant for
        // an equivalent run. The sub-agent has no inbound user row (its task is the spawn request,
        // not a user message), so parity is asserted on the roles it CAN have: the sub-agent must
        // not be missing a role the normal session records for the same response. Reverting the
        // assistant append reddens exactly this test by name.
        var response = new AgentResponse
        {
            Content = "summary",
            ToolCalls =
            [
                new AgentToolCallInfo("call-a", "read", IsError: false, Arguments: """{"path":"x"}""", ResultContent: "ok")
            ]
        };

        var (manager, store) = CreateManager(response);
        var info = await manager.SpawnAsync(BuildRequest());
        await WaitForCompletionAsync(manager, info.SubAgentId);

        var subAgentRoles = (await ChildHistoryAsync(store, info.ChildSessionId))
            .Select(e => e.Role.ToString())
            .ToHashSet(StringComparer.Ordinal);

        // The normal-session reference: the shape every other blocking boundary writes.
        var normalRoles = new HashSet<string>(StringComparer.Ordinal)
        {
            MessageRole.Tool.ToString(),
            MessageRole.Assistant.ToString()
        };

        var missing = normalRoles.Except(subAgentRoles).ToList();
        missing.ShouldBeEmpty(
            $"sub-agent session is missing role(s) a normal session records: {string.Join(", ", missing)}");
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<IReadOnlyList<SessionEntry>> ChildHistoryAsync(ISessionStore store, SessionId childSessionId)
    {
        var session = await store.GetAsync(childSessionId, CancellationToken.None);
        session.ShouldNotBeNull();
        return session!.GetHistorySnapshot();
    }

    /// <summary>
    /// Polls the manager until the run leaves <see cref="SubAgentStatus.Running"/>. The run loop is
    /// fire-and-forget by design, so the test must observe the terminal state rather than await it.
    /// </summary>
    private static async Task<SubAgentInfo> WaitForCompletionAsync(ISubAgentManager manager, string subAgentId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var info = await manager.GetAsync(subAgentId);
            if (info is not null && info.Status != SubAgentStatus.Running)
                return info;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Sub-agent '{subAgentId}' never reached a terminal state.");
    }

    private static SubAgentSpawnRequest BuildRequest() => new()
    {
        ParentAgentId = AgentId.From("parent-agent"),
        ParentSessionId = SessionId.From(ParentSession),
        Task = "investigate",
        Mode = new Embody(SubAgentArchetype.General),
        InheritedConversationId = ConversationId.From("c_parent3256")
    };

    private static (DefaultSubAgentManager Manager, InMemorySessionStore Store) CreateManager(AgentResponse response)
    {
        var store = new InMemorySessionStore();
        return (BuildManager(store, response), store);
    }

    private static DefaultSubAgentManager CreateManagerWithoutStore(AgentResponse response)
        => BuildManager(sessionStore: null, response);

    private static DefaultSubAgentManager BuildManager(ISessionStore? sessionStore, AgentResponse response)
    {
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.AgentId).Returns(AgentId.From("child-agent"));
        handle.Setup(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("parent-agent"),
            DisplayName = "Parent",
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot"
        });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance,
            sessionStore: sessionStore);
    }
}
