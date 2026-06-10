using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Gateway.Tests.Isolation;

public class GatewayCallbackServerTests
{
    [Fact]
    public async Task StartAsync_BindsToAvailablePort()
    {
        var server = new GatewayCallbackServer(
            NullLogger<GatewayCallbackServer>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(server.Port > 0);
            Assert.True(server.IsListening);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_StopsListening()
    {
        var server = new GatewayCallbackServer(
            NullLogger<GatewayCallbackServer>.Instance);

        await server.StartAsync(CancellationToken.None);
        await server.StopAsync(CancellationToken.None);

        Assert.False(server.IsListening);
    }

    [Fact]
    public async Task GetCallbackUrl_ReturnsLocalhostWithPort()
    {
        var server = new GatewayCallbackServer(
            NullLogger<GatewayCallbackServer>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            var url = server.GetCallbackUrl();
            Assert.StartsWith("http://host.docker.internal:", url);
            Assert.Contains(server.Port.ToString(), url);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RegisterCallback_AllowsSandboxToReachGateway()
    {
        var server = new GatewayCallbackServer(
            NullLogger<GatewayCallbackServer>.Instance);
        var agentId = AgentId.From("test-agent");

        await server.StartAsync(CancellationToken.None);
        try
        {
            server.RegisterAgent(agentId, "agent-test-agent");
            Assert.True(server.HasRegisteredAgent(agentId));
        }
        finally
        {
            server.UnregisterAgent(agentId);
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnregisterAgent_RemovesFromCallback()
    {
        var server = new GatewayCallbackServer(
            NullLogger<GatewayCallbackServer>.Instance);
        var agentId = AgentId.From("test-agent");

        await server.StartAsync(CancellationToken.None);
        try
        {
            server.RegisterAgent(agentId, "agent-test-agent");
            server.UnregisterAgent(agentId);
            Assert.False(server.HasRegisteredAgent(agentId));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void GetCallbackUrl_ThrowsWhenNotStarted()
    {
        var server = new GatewayCallbackServer(
            NullLogger<GatewayCallbackServer>.Instance);

        Assert.Throws<InvalidOperationException>(() => server.GetCallbackUrl());
    }
}
