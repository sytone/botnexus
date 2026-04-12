using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Federation;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
                        AllowedAgents = ["leela"]
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
        registry.Setup(r => r.Contains(AgentId.From("leela"))).Returns(true);
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync("Hello from world-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "Hello back from world-b" });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(AgentId.From("leela"), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var sessions = new InMemorySessionStore();

        var controller = new CrossWorldFederationController(
            registry.Object,
            supervisor.Object,
            sessions,
            new CrossWorldInboundAuthService(platformConfig),
            platformConfig)
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
                SourceAgentId = "nova",
                TargetAgentId = "leela",
                Message = "Hello from world-a",
                ConversationId = "nova::cross::leela::abc123"
            },
            CancellationToken.None);

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<CrossWorldRelayResponse>().Subject;
        payload.Response.Should().Be("Hello back from world-b");
        payload.SessionId.Should().NotBeNullOrWhiteSpace();

        var savedSession = await sessions.GetAsync(SessionId.From(payload.SessionId));
        savedSession.Should().NotBeNull();
        savedSession!.ChannelType.Should().Be(ChannelKey.From("cross-world"));
        savedSession.Participants.Should().ContainSingle(p => p.Id == "nova" && p.WorldId == "world-a");
        savedSession.Participants.Should().ContainSingle(p => p.Id == "leela" && p.WorldId == "world-b");
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
                        AllowedAgents = ["leela"]
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
        registry.Setup(r => r.Contains(AgentId.From("leela"))).Returns(true);
        var controller = new CrossWorldFederationController(
            registry.Object,
            Mock.Of<IAgentSupervisor>(),
            new InMemorySessionStore(),
            new CrossWorldInboundAuthService(platformConfig),
            platformConfig)
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
                SourceAgentId = "nova",
                TargetAgentId = "leela",
                Message = "hello",
                ConversationId = "c-1"
            },
            CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }
}
