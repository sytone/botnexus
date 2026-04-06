using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ChannelsControllerTests
{
    [Fact]
    public void List_WithRegisteredAdapters_ReturnsMappedAdapterDtos()
    {
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(value => value.ChannelType).Returns("websocket");
        adapter.SetupGet(value => value.DisplayName).Returns("WebSocket");
        adapter.SetupGet(value => value.IsRunning).Returns(true);
        adapter.SetupGet(value => value.SupportsStreaming).Returns(true);
        adapter.SetupGet(value => value.SupportsSteering).Returns(true);
        adapter.SetupGet(value => value.SupportsFollowUp).Returns(false);
        adapter.SetupGet(value => value.SupportsThinkingDisplay).Returns(true);
        adapter.SetupGet(value => value.SupportsToolDisplay).Returns(false);

        var manager = new Mock<IChannelManager>();
        manager.SetupGet(value => value.Adapters).Returns([adapter.Object]);

        var controller = new ChannelsController(manager.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ChannelAdapterResponse>;
        payload.Should().NotBeNull();
        payload!.Should().ContainSingle();
        payload[0].Should().BeEquivalentTo(new ChannelAdapterResponse(
            Name: "websocket",
            DisplayName: "WebSocket",
            IsRunning: true,
            SupportsStreaming: true,
            SupportsSteering: true,
            SupportsFollowUp: false,
            SupportsThinking: true,
            SupportsToolDisplay: false));
    }
}
