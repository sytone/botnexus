using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class CronSupervisorCacheTests
{
    [Fact]
    public async Task GetOrCreateAsync_SameDescriptor_CronPinActivationAndRemovalRefreshOnlyWhenNeeded()
    {
        var agentId = AgentId.From("test");
        var sessionId = SessionId.From("test-soul");
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor { AgentId = agentId, DisplayName = "Test", ApiProvider = "p", ModelId = "first", IsolationStrategy = "test" });
        var session = new GatewaySession { AgentId = agentId, SessionId = sessionId };
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetAsync(sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var handles = new List<Mock<IAgentHandle>>();
        var pins = new List<string?>();
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentDescriptor _, AgentExecutionContext context, CancellationToken _) =>
            {
                pins.Add(context.Parameters.TryGetValue("cronModelOverride", out var pin) ? pin?.ToString() : null);
                var handle = new Mock<IAgentHandle>();
                handle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
                handles.Add(handle);
                return handle.Object;
            });
        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], sessions.Object, NullLogger<DefaultAgentSupervisor>.Instance);
        var original = await supervisor.GetOrCreateAsync(agentId, sessionId);
        session.Metadata["modelOverride"] = "p/first";
        session.Metadata["cronModelOverride"] = "p/first";
        var pinned = await supervisor.GetOrCreateAsync(agentId, sessionId);
        pinned.ShouldNotBeSameAs(original);
        (await supervisor.GetOrCreateAsync(agentId, sessionId)).ShouldBeSameAs(pinned);
        handles.Count.ShouldBe(2);
        session.Metadata["cronModelOverride"] = "p/second";
        session.Metadata["modelOverride"] = "p/second";
        var changed = await supervisor.GetOrCreateAsync(agentId, sessionId);
        changed.ShouldNotBeSameAs(pinned);
        session.Metadata.Remove("cronModelOverride");
        // Keep the descriptor identical to prove pin removal independently invalidates.
        var unpinned = await supervisor.GetOrCreateAsync(agentId, sessionId);
        unpinned.ShouldNotBeSameAs(changed);
        (await supervisor.GetOrCreateAsync(agentId, sessionId)).ShouldBeSameAs(unpinned);
        pins.ShouldBe(new string?[] { null, "p/first", "p/second", null });
        foreach (var stale in handles.Take(3)) stale.Verify(h => h.DisposeAsync(), Times.Once);
        await supervisor.StopAllAsync();
    }
}
