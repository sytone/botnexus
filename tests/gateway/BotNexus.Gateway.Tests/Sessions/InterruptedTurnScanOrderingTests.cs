using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Regression tests for #2030: the interrupted-turn scan must run AFTER agent registration.
/// The previous implementation ran the scan in <c>IHostedService.StartAsync</c>, which the
/// generic host invokes in registration order - so the scan saw an empty registry (agents are
/// registered by later hosted services) and always no-opped. Migrating to
/// <see cref="IHostedLifecycleService.StartedAsync"/> guarantees the registry is fully populated
/// before the scan runs.
/// </summary>
public sealed class InterruptedTurnScanOrderingTests
{
    /// <summary>
    /// A hosted service that registers an agent during its own StartAsync - modelling
    /// AgentConfigurationHostedService, which populate the
    /// registry only once the host starts hosted services.
    /// </summary>
    private sealed class LateAgentRegistrationService(IAgentRegistry registry, string agentId) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            registry.Register(new AgentDescriptor
            {
                AgentId = AgentId.From(agentId),
                DisplayName = agentId,
                ModelId = "gpt-4.1",
                ApiProvider = "copilot"
            });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Scan_RunsAfterAgentRegistration_DetectsPreExistingSentinel()
    {
        const string agentId = "farnsworth";
        var session = new GatewaySession
        {
            SessionId = SessionId.From("sess-orphan"),
            AgentId = AgentId.From(agentId),
            Status = SessionStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow,
            SessionType = SessionType.UserAgent
        };
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress - gateway restarted if visible]",
            IsCrashSentinel = true
        });

        var store = new Mock<ISessionStore>();
        store.Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentId? id, CancellationToken _) =>
                (!id.HasValue || session.AgentId == id.Value)
                    ? new List<GatewaySession> { session }
                    : new List<GatewaySession>());
        store.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // A real registry that starts EMPTY - agents are added only when the late hosted
        // service's StartAsync runs, exactly as production does.
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IAgentRegistry>(registry);
        builder.Services.AddSingleton(store.Object);
        builder.Services.AddSingleton(Mock.Of<IActivityBroadcaster>());
        builder.Services.AddSingleton(Mock.Of<IChannelManager>());

        // Register the late agent-registration service FIRST so it runs before the scan's
        // StartAsync would - proving the scan no longer depends on StartAsync ordering.
        builder.Services.AddHostedService(sp =>
            new LateAgentRegistrationService(sp.GetRequiredService<IAgentRegistry>(), agentId));
        builder.Services.AddHostedService(sp => new InterruptedTurnNotificationService(
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<IActivityBroadcaster>(),
            sp.GetRequiredService<IChannelManager>(),
            NullLogger<InterruptedTurnNotificationService>.Instance));

        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();

        // The scan detected the pre-existing sentinel for the late-registered agent, cleared
        // it, and appended a notification - proving it ran after registration.
        session.History.ShouldNotContain(e => e.IsCrashSentinel);
        session.History.ShouldContain(e => e.Role == MessageRole.Notification);
        store.Verify(s => s.SaveAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }
}
