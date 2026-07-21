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
/// RED tests proving the orphan-window bug in <see cref="DefaultSubAgentManager.SpawnAsync"/>:
/// the parent-conversation pinning currently happens inside the fire-and-forget
/// <c>Task.Run(...)</c> at the end of <c>SpawnAsync</c>. That creates a window where the
/// child session exists with <c>ConversationId == null</c> — a real F-6 orphan caller
/// like <see cref="ISessionStore.ListByConversationAsync"/> can't see it, and any
/// concurrent reader (e.g. the canvas, the conversation list, /api/conversations history)
/// sees a "ghost" session not linked to its parent conversation.
/// </summary>
/// <remarks>
/// These tests pin Phase 4 item 2 / F-6: <c>SubAgentSpawnRequest.InheritedConversationId</c>
/// is required, and pinning must be eager (in <c>SpawnAsync</c> itself, not async-after).
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
        childSession.ConversationId.ShouldBe(ConversationId.From("conv-materialized"));
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
            "After SpawnAsync returns, the child session must already be bound to the parent conversation. " +
            "If you needed Task.Delay(...) here, pinning is still lazy and the orphan-window bug remains.");
        pinnedSession.Session.ConversationId.Value.ShouldBe("conv-99");
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

        // Assert: the recorded order MUST start with the pin and only then prompt.
        events.ShouldNotBeEmpty();
        events[0].ShouldBe("pin:conv-order",
            "Conversation pinning must complete strictly before PromptAsync runs. " +
            "Recorded order: " + string.Join(" -> ", events));
        events.ShouldContain("prompt");
        events.IndexOf("pin:conv-order").ShouldBeLessThan(events.IndexOf("prompt"));
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
