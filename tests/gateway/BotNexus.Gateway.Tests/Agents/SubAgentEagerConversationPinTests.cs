using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Pins the F-6 eager-materialisation contract for <see cref="DefaultSubAgentManager.SpawnAsync"/>:
/// the child session's <c>ConversationId</c> must be assigned on the synchronous spawn path, not
/// inside the fire-and-forget <c>Task.Run(...)</c>. Lazy assignment leaves a window where the child
/// session exists with <c>ConversationId == null</c> - a real F-6 orphan that
/// <see cref="ISessionStore.ListByConversationAsync"/> cannot see, and that any concurrent reader
/// (canvas, conversation list, /api/conversations history) observes as a ghost session.
/// </summary>
/// <remarks>
/// Since #2338 the value assigned is the child run's <em>own</em> minted conversation id, not the
/// parent's: these tests therefore assert that the child session is bound to <em>something</em>
/// eagerly and that it is explicitly NOT the parent's id. The parent link now lives on
/// <c>Conversation.ParentConversationId</c> (see <c>SubAgentOwnConversationTests</c>).
/// </remarks>
public sealed class SubAgentEagerConversationPinTests
{
    [Fact]
    public async Task SpawnAsync_MaterializesChildSessionBeforeHandleCreation()
    {
        var events = new List<string>();
        var childSession = new GatewaySession();
        var sessionStore = new Mock<ISessionStore>();
        sessionStore.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GatewaySession?)null);
        sessionStore.Setup(s => s.GetOrCreateAsync(
                It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("create-session"))
            .ReturnsAsync(childSession);
        sessionStore.Setup(s => s.SaveAsync(childSession, It.IsAny<CancellationToken>()))
            .Callback(() => events.Add("save-session"))
            .Returns(Task.CompletedTask);

        var handle = BuildHandle();
        var manager = BuildManager(handle, sessionStore: sessionStore.Object,
            onHandleCreate: () => events.Add("create-handle"));
        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "x",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("conv-materialized")
        };

        await manager.SpawnAsync(request);

        events.IndexOf("create-session").ShouldBeLessThan(events.IndexOf("create-handle"));
        events.IndexOf("save-session").ShouldBeLessThan(events.IndexOf("create-handle"));
        childSession.SessionType.ShouldBe(SessionType.AgentSubAgent);
        childSession.ConversationId.IsInitialized().ShouldBeTrue();
        // #2338: the child owns its conversation; it must NOT be the parent's.
        childSession.ConversationId.ShouldNotBe(ConversationId.From("conv-materialized"));
    }

    [Fact]
    public async Task SpawnAsync_DoesNotReturnUntilConversationIsPinned()
    {
        // Arrange: a session store whose GetAsync blocks on a TCS we control.
        // If pinning is EAGER (post-fix), SpawnAsync awaits GetAsync and therefore must
        // not complete until we release the TCS.
        // If pinning is LAZY (current bug, inside Task.Run), SpawnAsync returns
        // immediately because Task.Run is fire-and-forget.
        var releaseGet = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(async (SessionId _, CancellationToken _) =>
            {
                await releaseGet.Task.ConfigureAwait(false);
                return new GatewaySession();
            });
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handle = BuildHandle();
        var manager = BuildManager(handle, sessionStore: sessionStore.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "x",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("conv-1")
        };

        // Act
        var spawnTask = manager.SpawnAsync(request);
        var finishedEarly = await Task.WhenAny(spawnTask, Task.Delay(500)) == spawnTask;

        // Assert: SpawnAsync must NOT have completed yet — it's still awaiting the
        // ConversationId pin which we've blocked. Currently this assertion FAILS
        // (RED) because Task.Run lets SpawnAsync return immediately while pinning
        // dangles in the background.
        try
        {
            finishedEarly.ShouldBeFalse(
                "SpawnAsync must await ConversationId pinning before returning. " +
                "Today, pinning is queued via Task.Run, leaving the child session " +
                "orphan-listable from the moment SpawnAsync returns until the " +
                "background task gets scheduled.");
        }
        finally
        {
            // Cleanup so the background task can finish and not leak across tests.
            releaseGet.SetResult(true);
            await spawnTask;
        }
    }

    [Fact]
    public async Task SpawnAsync_ConversationIdIsSet_OnChildSession_BeforeReturn()
    {
        // Arrange
        var pinnedSession = new GatewaySession();
        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pinnedSession);
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // A handle whose PromptAsync hangs forever. If pinning is eager, the
        // assertion below passes BEFORE the prompt ever runs.
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.AgentId).Returns(AgentId.From("child-agent"));
        handle.Setup(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return new AgentResponse { Content = "never" };
            });

        var manager = BuildManager(handle, sessionStore: sessionStore.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "x",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("conv-99")
        };

        // Act
        await manager.SpawnAsync(request);

        // Assert: the moment SpawnAsync returns, the child session must already be
        // pinned to the parent conversation. NO Task.Delay needed.
        pinnedSession.Session.ConversationId.IsInitialized().ShouldBeTrue(
            "After SpawnAsync returns, the child session must already be bound to its own conversation. " +
            "If you needed Task.Delay(...) here, the binding is still lazy and the orphan-window bug remains.");
        // #2338: distinct identity - the parent's id is the parent edge, not the child's id.
        pinnedSession.Session.ConversationId.Value.ShouldNotBe("conv-99");
    }

    [Fact]
    public async Task SpawnAsync_PinsConversation_BEFORE_PromptIsInvoked()
    {
        // Arrange: prompt records the order of events. Pinning must come first.
        var events = new List<string>();
        var pinnedSession = new GatewaySession();

        var sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pinnedSession);
        sessionStore
            .Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Callback<GatewaySession, CancellationToken>((sess, _) =>
                events.Add($"pin:{(sess.Session.ConversationId.IsInitialized() ? sess.Session.ConversationId.Value : "<unset>")}"))
            .Returns(Task.CompletedTask);

        var promptInvoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.AgentId).Returns(AgentId.From("child-agent"));
        handle.Setup(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken _) =>
            {
                events.Add("prompt");
                promptInvoked.TrySetResult(true);
                return new AgentResponse { Content = "done" };
            });

        var manager = BuildManager(handle, sessionStore: sessionStore.Object);

        var request = new SubAgentSpawnRequest
        {
            ParentAgentId = AgentId.From("parent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "go",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("conv-order")
        };

        // Act
        await manager.SpawnAsync(request);
        await promptInvoked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Assert: the recorded order MUST start with the bind and only then prompt.
        events.ShouldNotBeEmpty();
        events[0].ShouldStartWith("pin:c_");
        events[0].ShouldNotBe(
            "pin:conv-order",
            "Conversation binding must bind the child's OWN minted conversation id, never the " +
            "parent's (#2338). Recorded order: " + string.Join(" -> ", events));
        events.ShouldContain("prompt");
        events.IndexOf(events[0]).ShouldBeLessThan(events.IndexOf("prompt"));
    }

    private static Mock<IAgentHandle> BuildHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.AgentId).Returns(AgentId.From("child-agent"));
        handle.Setup(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "done" });
        return handle;
    }

    private static DefaultSubAgentManager BuildManager(
        Mock<IAgentHandle> handle,
        ISessionStore? sessionStore = null,
        Action? onHandleCreate = null)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Callback(() => onHandleCreate?.Invoke())
            .ReturnsAsync(handle.Object);

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns(CreateDescriptor());
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var activity = new Mock<IActivityBroadcaster>();
        activity.Setup(a => a.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var dispatcher = new Mock<IChannelDispatcher>();

        var options = new Mock<IOptionsMonitor<GatewayOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new GatewayOptions());

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            activity.Object,
            dispatcher.Object,
            options.Object,
            NullLogger<DefaultSubAgentManager>.Instance,
            sessionStore: sessionStore);
    }

    private static AgentDescriptor CreateDescriptor() => new()
    {
        AgentId = AgentId.From("parent"),
        DisplayName = "Parent",
        ModelId = "test-model",
        ApiProvider = "test-provider",
        SystemPrompt = "You are a test agent."
    };
}
