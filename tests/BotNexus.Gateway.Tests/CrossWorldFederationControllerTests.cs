using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Federation;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class CrossWorldFederationControllerTests
{
    [Fact]
    public async Task RelayAsync_WithValidInboundAuth_ReturnsResponseAndPersistsSession()
    {
        var platformConfig = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                World = new BotNexus.Domain.WorldIdentity { Id = "world-b", Name = "World B" },
                CrossWorldPermissions =
                [
                    new CrossWorldPermissionConfig
                    {
                        TargetWorldId = "world-a",
                        AllowInbound = true,
                        AllowedAgents = ["agent-c"]
                    }
                ],
                CrossWorld = new CrossWorldFederationConfig
                {
                    Inbound = new CrossWorldInboundConfig
                    {
                        Enabled = true,
                        AllowedWorlds = ["world-a"],
                        ApiKeys = new Dictionary<string, string> { ["world-a"] = "shared-key" }
                    }
                }
            }
        };

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains(AgentId.From("agent-c"))).Returns(true);
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync("Hello from world-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Hello back from world-b" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(AgentId.From("agent-c"), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();
        var platformConfigMonitor = new StaticOptionsMonitor<PlatformConfig>(platformConfig);

        var controller = new CrossWorldFederationController(
            registry.Object,
            supervisor.Object,
            sessions,
            new CrossWorldInboundAuthService(platformConfigMonitor),
            platformConfigMonitor)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.ControllerContext.HttpContext.Request.Headers["X-Cross-World-Key"] = "shared-key";

        var response = await controller.RelayAsync(
            new CrossWorldRelayRequest
            {
                SourceWorldId = "world-a",
                SourceAgentId = "test-agent",
                TargetAgentId = "agent-c",
                Message = "Hello from world-a",
                ChannelAddress = "nova::cross::leela::abc123"
            },
            CancellationToken.None);

        var ok = response.Result.ShouldBeOfType<OkObjectResult>();
        var payload = ok.Value.ShouldBeOfType<CrossWorldRelayResponse>();
        payload.Response.ShouldBe("Hello back from world-b");
        payload.SessionId.ShouldNotBeNullOrWhiteSpace();

        var savedSession = await sessions.GetAsync(SessionId.From(payload.SessionId));
        savedSession.ShouldNotBeNull();
        savedSession!.ChannelType.ShouldBe(ChannelKey.From("cross-world"));
        savedSession.Participants.Where(p => p.Id == "test-agent" && p.WorldId == "world-a").ShouldHaveSingleItem();
        savedSession.Participants.Where(p => p.Id == "agent-c" && p.WorldId == "world-b").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task RelayAsync_WithInvalidApiKey_ReturnsUnauthorized()
    {
        var platformConfig = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                CrossWorldPermissions =
                [
                    new CrossWorldPermissionConfig
                    {
                        TargetWorldId = "world-a",
                        AllowInbound = true,
                        AllowedAgents = ["agent-c"]
                    }
                ],
                CrossWorld = new CrossWorldFederationConfig
                {
                    Inbound = new CrossWorldInboundConfig
                    {
                        Enabled = true,
                        AllowedWorlds = ["world-a"],
                        ApiKeys = new Dictionary<string, string> { ["world-a"] = "shared-key" }
                    }
                }
            }
        };

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Contains(AgentId.From("agent-c"))).Returns(true);
        var platformConfigMonitor2 = new StaticOptionsMonitor<PlatformConfig>(platformConfig);
        var controller = new CrossWorldFederationController(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            new CrossWorldInboundAuthService(platformConfigMonitor2),
            platformConfigMonitor2)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.ControllerContext.HttpContext.Request.Headers["X-Cross-World-Key"] = "wrong";

        var result = await controller.RelayAsync(
            new CrossWorldRelayRequest
            {
                SourceWorldId = "world-a",
                SourceAgentId = "test-agent",
                TargetAgentId = "agent-c",
                Message = "hello",
                ChannelAddress = "c-1"
            },
            CancellationToken.None);

        result.Result.ShouldBeOfType<UnauthorizedObjectResult>();
    }
}

file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
