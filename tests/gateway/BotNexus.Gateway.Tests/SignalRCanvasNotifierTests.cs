using BotNexus.Extensions.Channels.SignalR;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class SignalRCanvasNotifierTests
{
    [Fact]
    public async Task NotifyCanvasUpdatedAsync_BroadcastsCanvasUpdatedArguments()
    {
        var clientProxy = new Mock<IGatewayHubClient>();
        clientProxy.Setup(proxy => proxy.CanvasUpdated(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients<IGatewayHubClient>>();
        clients.Setup(value => value.All).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<GatewayHub, IGatewayHubClient>>();
        hubContext.SetupGet(value => value.Clients).Returns(clients.Object);

        var notifier = new SignalRCanvasNotifier(hubContext.Object);

        await notifier.NotifyCanvasUpdatedAsync("agent-a", "<p>hello</p>");

        clientProxy.Verify(proxy => proxy.CanvasUpdated(
                "agent-a",
                "<p>hello</p>"),
            Times.Once);
    }
}
