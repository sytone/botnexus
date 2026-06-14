using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using BotNexus.Gateway.Tests;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// Behavioural guards for the #1385 consolidation of the six parallel per-sub-agent dictionaries
/// into a single <c>SubAgentRecord</c>. The most important guarantee is that the child <c>AgentId</c>
/// is always available on the live record at completion time — the old design carried it in a
/// separate <c>_childAgentIds</c> map that could drift, which is why <c>OnCompletedAsync</c> needed a
/// synthetic <c>subagent:&lt;id&gt;</c> fallback sender. With the record there is no second map to drift,
/// so the completion wake-up must always carry the real child id and never the synthetic fallback.
/// </summary>
public sealed class SubAgentRecordConsolidationTests
{
    [Fact]
    public async Task OnCompleted_WakeUp_CarriesRealChildAgentId_NotSyntheticFallback()
    {
        var manager = CreateManager(out var dispatcher, out _);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        InboundMessage? dispatched = null;
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((message, _) => dispatched = message)
            .Returns(Task.CompletedTask);

        await manager.OnCompletedAsync(spawned.SubAgentId, "done");

        dispatched.ShouldNotBeNull();
        // The producer-side sender must be the real child agent id minted at spawn
        // (parent-agent--subagent--General--<uniqueId>), proving the record preserved it.
        dispatched!.Sender.Kind.ShouldBe(CitizenKind.Agent);
        dispatched.Sender.Value.ShouldStartWith("parent-agent--subagent--General--");
        // It must NOT fall back to the synthetic id the old drift-prone path used.
        dispatched.Sender.Value.ShouldNotStartWith("subagent:");
    }

    [Fact]
    public async Task OnCompleted_CalledTwice_DispatchesWakeUpExactlyOnce()
    {
        var manager = CreateManager(out var dispatcher, out _);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await manager.OnCompletedAsync(spawned.SubAgentId, "first");
        await manager.OnCompletedAsync(spawned.SubAgentId, "second");

        // The completion once-only gate now lives on the record; the second call is a no-op.
        dispatcher.Verify(
            d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();
        info!.ResultSummary.ShouldBe("first");
    }

    [Fact]
    public async Task Kill_ThenComplete_StopsChildSupervisorExactlyOnce()
    {
        var manager = CreateManager(out var dispatcher, out var supervisor);
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From("parent-session"));
        // A late completion arriving after the kill must not re-run child cleanup.
        await manager.OnCompletedAsync(spawned.SubAgentId, "late");

        killed.ShouldBeTrue();
        // CleanupChildAgentAsync's once-only gate (formerly _childAgentIds.TryRemove succeeding
        // exactly once) is now record.TryBeginCleanup — the child supervisor is stopped once.
        supervisor.Verify(
            s => s.StopAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.IsAny<SessionId>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();
        // Killed status is terminal — the late completion does not overwrite it.
        info!.Status.ShouldBe(SubAgentStatus.Killed);
    }

    [Fact]
    public async Task Kill_SetsKilledStatus_AndStopsChild()
    {
        var manager = CreateManager(out _, out var supervisor);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        var killed = await manager.KillAsync(spawned.SubAgentId, SessionId.From("parent-session"));
        var info = await manager.GetAsync(spawned.SubAgentId);

        killed.ShouldBeTrue();
        info.ShouldNotBeNull();
        info!.Status.ShouldBe(SubAgentStatus.Killed);
        info.CompletedAt.ShouldNotBeNull();
        supervisor.Verify(
            s => s.StopAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                spawned.ChildSessionId,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Get_ReturnsSubAgent_AfterCompletion_RecordRemainsQueryable()
    {
        var manager = CreateManager(out var dispatcher, out _);
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var spawned = await manager.SpawnAsync(CreateSpawnRequest());

        await manager.OnCompletedAsync(spawned.SubAgentId, "done");

        // Consolidating into one record must not change visibility: a completed sub-agent stays
        // queryable (the satellite maps were removed, but the primary record is retained as before).
        var info = await manager.GetAsync(spawned.SubAgentId);
        info.ShouldNotBeNull();
        info!.Status.ShouldBe(SubAgentStatus.Completed);

        var listed = await manager.ListAsync(SessionId.From("parent-session"));
        listed.ShouldContain(i => i.SubAgentId == spawned.SubAgentId);
    }

    private static DefaultSubAgentManager CreateManager(
        out Mock<IChannelDispatcher> dispatcher,
        out Mock<IAgentSupervisor> supervisor)
    {
        var childHandle = CreateHangingHandle();
        supervisor = new Mock<IAgentSupervisor>();
        supervisor
            .Setup(s => s.GetOrCreateAsync(
                It.Is<AgentId>(id => id.Value.StartsWith("parent-agent--subagent--", StringComparison.Ordinal)),
                It.Is<SessionId>(id => id.Value.Contains("::subagent::", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(childHandle.Object);
        supervisor
            .Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
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

        dispatcher = new Mock<IChannelDispatcher>();

        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            dispatcher.Object,
            new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
            NullLogger<DefaultSubAgentManager>.Instance);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest()
        => new()
        {
            ParentAgentId = AgentId.From("parent-agent"),
            ParentSessionId = SessionId.From("parent-session"),
            Task = "Do background work",
            Mode = new Embody(SubAgentArchetype.General),
            InheritedConversationId = ConversationId.From("inherited-conv")
        };

    private static Mock<IAgentHandle> CreateHangingHandle()
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("parent-agent"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("child-session"));
        handle.SetupGet(h => h.IsRunning).Returns(true);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentResponse { Content = "never" };
            });
        return handle;
    }
}
