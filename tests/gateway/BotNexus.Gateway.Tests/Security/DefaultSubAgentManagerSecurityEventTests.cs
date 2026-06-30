using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Tests for the security-event emissions wired into <see cref="DefaultSubAgentManager"/>
/// (Step 4/5 of the security-event taxonomy, issue #1647 / #1526). A successful sub-agent
/// spawn and a successful kill each emit exactly one <see cref="SecurityEvent"/> to the trusted
/// <see cref="ISecurityEventSink"/>, carrying category/action/hashed-actor/target/control. A kill
/// that does not transition the sub-agent (wrong requester, already terminal) emits nothing, and
/// emission must never break the spawn/kill path.
/// </summary>
public sealed class DefaultSubAgentManagerSecurityEventTests
{
    private const string ParentSessionId = "parent-session";

    // -- Spawn emits ----------------------------------------------------

    [Fact]
    public async Task SpawnAsync_EmitsExactlyOneSubagentSpawnedEvent()
    {
        var sink = new RecordingSink();
        var manager = CreateManager(CreateHangingHandle(), sink);

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var spawnEvents = sink.Events.Where(e => e.Action == "subagent.spawned").ToList();
        spawnEvents.Count.ShouldBe(1);
        var evt = spawnEvents[0];
        evt.Category.ShouldBe(SecurityEventCategory.Tool);
        evt.Outcome.ShouldBe(SecurityEventOutcome.Success);
        evt.Severity.ShouldBe(SecurityEventSeverity.Info);
        evt.Control.ShouldBe(SecurityControlFamily.Sandbox);

        evt.Actor.ShouldNotBeNull();
        evt.Actor!.Kind.ShouldBe(SecurityActorKind.Agent);
        // Actor id is a hash of the parent session id - never the raw id.
        evt.Actor.Id.ShouldNotBe(ParentSessionId);
        evt.Actor.Id.ShouldNotContain(ParentSessionId);
        evt.Actor.Id.ShouldNotBeNullOrWhiteSpace();

        evt.Target.ShouldNotBeNull();
        evt.Target!.Kind.ShouldBe(SecurityTargetKind.Tool);
        evt.Target.Reference.ShouldBe(spawned.SubAgentId);
    }

    // -- Kill emits -----------------------------------------------------

    [Fact]
    public async Task KillAsync_WhenSuccessful_EmitsExactlyOneSubagentKilledEvent()
    {
        var sink = new RecordingSink();
        var manager = CreateManager(CreateHangingHandle(), sink);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From(ParentSessionId));
        killed.ShouldBeTrue();

        var killEvents = sink.Events.Where(e => e.Action == "subagent.killed").ToList();
        killEvents.Count.ShouldBe(1);
        var evt = killEvents[0];
        evt.Category.ShouldBe(SecurityEventCategory.Tool);
        evt.Outcome.ShouldBe(SecurityEventOutcome.Success);
        evt.Severity.ShouldBe(SecurityEventSeverity.Info);
        evt.Control.ShouldBe(SecurityControlFamily.Sandbox);

        evt.Actor.ShouldNotBeNull();
        evt.Actor!.Kind.ShouldBe(SecurityActorKind.Agent);
        evt.Actor.Id.ShouldNotBe(ParentSessionId);
        evt.Actor.Id.ShouldNotContain(ParentSessionId);

        evt.Target.ShouldNotBeNull();
        evt.Target!.Kind.ShouldBe(SecurityTargetKind.Tool);
        evt.Target.Reference.ShouldBe(spawned.SubAgentId);
    }

    [Fact]
    public async Task KillAsync_WhenWrongRequester_EmitsNoKilledEvent()
    {
        var sink = new RecordingSink();
        var manager = CreateManager(CreateHangingHandle(), sink);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        // A session that does not own the sub-agent cannot kill it - no transition, no event.
        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From("a-different-session"));

        killed.ShouldBeFalse();
        sink.Events.ShouldNotContain(e => e.Action == "subagent.killed");
    }

    [Fact]
    public async Task KillAsync_WhenAlreadyKilled_EmitsExactlyOneKilledEvent()
    {
        var sink = new RecordingSink();
        var manager = CreateManager(CreateHangingHandle(), sink);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        (await manager.KillAsync(spawned.SubAgentId, SessionId.From(ParentSessionId))).ShouldBeTrue();
        // Second kill on an already-terminal sub-agent early-returns false and must not re-emit.
        (await manager.KillAsync(spawned.SubAgentId, SessionId.From(ParentSessionId))).ShouldBeFalse();

        sink.Events.Count(e => e.Action == "subagent.killed").ShouldBe(1);
    }

    // -- Best-effort / never breaks the path ----------------------------

    [Fact]
    public async Task SpawnAsync_WhenSinkThrows_StillSpawns()
    {
        var manager = CreateManager(CreateHangingHandle(), new ThrowingSink());

        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        spawned.SubAgentId.ShouldNotBeNullOrWhiteSpace();
        (await manager.GetAsync(spawned.SubAgentId))!.Status.ShouldBe(SubAgentStatus.Running);
    }

    [Fact]
    public async Task KillAsync_WhenSinkThrows_StillKills()
    {
        var manager = CreateManager(CreateHangingHandle(), new ThrowingSink());
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From(ParentSessionId));

        killed.ShouldBeTrue();
    }

    [Fact]
    public async Task NullSink_DoesNotEmitAndStillSpawnsAndKills()
    {
        var manager = CreateManager(CreateHangingHandle(), securityEvents: null);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        (await manager.KillAsync(spawned.SubAgentId, SessionId.From(ParentSessionId))).ShouldBeTrue();
    }

    // -- Test harness ---------------------------------------------------

    private static DefaultSubAgentManager CreateManager(
        Mock<IAgentHandle> childHandle,
        ISecurityEventSink? securityEvents)
    {
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.Is<SessionId>(id => id.Value.Contains("::subagent::", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.StopAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.IsAny<SessionId>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var registry = new Mock<IAgentRegistry>();
        registry
            .Setup(r => r.Get(AgentId.From("parent-agent")))
            .Returns(new AgentDescriptor
            {
                AgentId = AgentId.From("parent-agent"),
                DisplayName = "Parent Agent",
                ModelId = "gpt-5-mini",
                ApiProvider = "copilot"
            });

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            new NoopActivityBroadcaster(),
            Mock.Of<IChannelDispatcher>(),
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance,
            securityEvents: securityEvents);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From(ParentSessionId),
            Task = "Do background work",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

    private static Mock<IAgentHandle> CreateHangingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        handle.Setup(h => h.FollowUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        handle.Setup(h => h.FollowUpAsync(It.IsAny<AgentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return handle;
    }

    private sealed class RecordingSink : ISecurityEventSink
    {
        private readonly Lock _sync = new();
        private readonly List<SecurityEvent> _events = [];

        public IReadOnlyList<SecurityEvent> Events
        {
            get
            {
                lock (_sync)
                {
                    return [.. _events];
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _events.Count;
                }
            }
        }

        public void Record(SecurityEvent securityEvent)
        {
            lock (_sync)
            {
                _events.Add(securityEvent);
            }
        }

        public IReadOnlyList<SecurityEvent> Snapshot() => Events;

        public void Clear()
        {
            lock (_sync)
            {
                _events.Clear();
            }
        }
    }

    private sealed class ThrowingSink : ISecurityEventSink
    {
        public int Count => 0;
        public void Record(SecurityEvent securityEvent) => throw new InvalidOperationException("sink down");
        public IReadOnlyList<SecurityEvent> Snapshot() => [];
        public void Clear() { }
    }

    private sealed class NoopActivityBroadcaster : IActivityBroadcaster
    {
        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
