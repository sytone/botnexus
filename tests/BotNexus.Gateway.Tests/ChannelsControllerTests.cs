using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ChannelsControllerTests
{
    [Fact]
    public void List_WithNoRegisteredAdapters_ReturnsEmptyList()
    {
        var controller = CreateController();

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ChannelAdapterResponse>;
        payload.Should().NotBeNull();
        payload.Should().BeEmpty();
    }

    [Fact]
    public void List_WithRegisteredAdapter_ReturnsMappedAdapterDto()
    {
        var controller = CreateController(CreateAdapter(
            channelType: "websocket",
            displayName: "WebSocket",
            isRunning: true,
            supportsStreaming: true,
            supportsSteering: true,
            supportsFollowUp: false,
            supportsThinkingDisplay: true,
            supportsToolDisplay: false));

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
            SupportsThinkingDisplay: true,
            SupportsToolDisplay: false));
    }

    [Fact]
    public void List_WithMultipleAdapters_ReturnsAllInRegistrationOrder()
    {
        var controller = CreateController(
            CreateAdapter("websocket", "WebSocket", true, true, true, false, true, false),
            CreateAdapter("telegram", "Telegram", false, false, false, true, false, true));

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ChannelAdapterResponse>;
        payload.Should().NotBeNull();
        payload!.Should().HaveCount(2);
        payload.Select(item => item.Name).Should().ContainInOrder("websocket", "telegram");
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, false, false, false, false)]
    public void List_MapsCapabilityFlagsCorrectly(
        bool supportsStreaming,
        bool supportsSteering,
        bool supportsFollowUp,
        bool supportsThinkingDisplay,
        bool supportsToolDisplay)
    {
        var controller = CreateController(CreateAdapter(
            channelType: "adapter",
            displayName: "Adapter",
            isRunning: true,
            supportsStreaming: supportsStreaming,
            supportsSteering: supportsSteering,
            supportsFollowUp: supportsFollowUp,
            supportsThinkingDisplay: supportsThinkingDisplay,
            supportsToolDisplay: supportsToolDisplay));

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ChannelAdapterResponse>;
        payload.Should().NotBeNull();
        payload![0].Should().BeEquivalentTo(new ChannelAdapterResponse(
            Name: "adapter",
            DisplayName: "Adapter",
            IsRunning: true,
            SupportsStreaming: supportsStreaming,
            SupportsSteering: supportsSteering,
            SupportsFollowUp: supportsFollowUp,
            SupportsThinkingDisplay: supportsThinkingDisplay,
            SupportsToolDisplay: supportsToolDisplay));
    }

    [Fact]
    public void List_ReturnsOkResultWithChannelAdapterResponsePayload()
    {
        var controller = CreateController(CreateAdapter(
            channelType: "cli",
            displayName: "CLI",
            isRunning: true,
            supportsStreaming: false,
            supportsSteering: false,
            supportsFollowUp: false,
            supportsThinkingDisplay: false,
            supportsToolDisplay: false));

        var result = controller.List();

        result.Result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result.Result!;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<ChannelAdapterResponse>>();
        ((IReadOnlyList<ChannelAdapterResponse>)ok.Value!).Should().OnlyContain(item => item is ChannelAdapterResponse);
    }

    private static ChannelsController CreateController(params IChannelAdapter[] adapters)
    {
        var manager = new Mock<IChannelManager>();
        manager.SetupGet(value => value.Adapters).Returns(adapters);
        return new ChannelsController(manager.Object);
    }

    private static IChannelAdapter CreateAdapter(
        string channelType,
        string displayName,
        bool isRunning,
        bool supportsStreaming,
        bool supportsSteering,
        bool supportsFollowUp,
        bool supportsThinkingDisplay,
        bool supportsToolDisplay)
    {
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(value => value.ChannelType).Returns(channelType);
        adapter.SetupGet(value => value.DisplayName).Returns(displayName);
        adapter.SetupGet(value => value.IsRunning).Returns(isRunning);
        adapter.SetupGet(value => value.SupportsStreaming).Returns(supportsStreaming);
        adapter.SetupGet(value => value.SupportsSteering).Returns(supportsSteering);
        adapter.SetupGet(value => value.SupportsFollowUp).Returns(supportsFollowUp);
        adapter.SetupGet(value => value.SupportsThinkingDisplay).Returns(supportsThinkingDisplay);
        adapter.SetupGet(value => value.SupportsToolDisplay).Returns(supportsToolDisplay);
        return adapter.Object;
    }
}
